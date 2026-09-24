using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

using Dle.Edge.Clients;
using Dle.Edge.Configuration;
using Dle.Edge.Rendering;

using Microsoft.Extensions.Options;

namespace Dle.SecurityTests.Fuzzing;

/// <summary>
/// The three fuzz targets §D.4 asks for: the URL parser, the user-agent parser and the
/// <c>routing_rules</c> deserializer.
/// </summary>
/// <remarks>
/// <para>
/// <b>SharpFuzz is not installed and these were not run under it.</b> §D.4 lists SharpFuzz for
/// nightly fuzzing; it needs libFuzzer and an instrumentation pass over the assemblies under test,
/// neither of which exists on this machine, and the package is not in the repository's central
/// version list. Adding it is a change to the solution's dependency set and to CI, not to a test
/// project, so what is here is the half that belongs in a test project: the targets themselves,
/// written to the shape SharpFuzz drives, plus a deterministic property-based run of the same code in
/// <see cref="FuzzHarnessTests"/> that does execute on every build.
/// </para>
/// <para>
/// To run them under SharpFuzz, add a small console project that references this one and calls
/// <c>Fuzzer.LibFuzzer.Run(FuzzTargets.RoutingRules)</c> (or one of the others), instrument the
/// assembly under test with <c>sharpfuzz</c>, and drive it with libFuzzer. Each target below takes
/// raw bytes, so no adaptation is needed.
/// </para>
/// <para>
/// <b>What a target asserts.</b> Nothing, deliberately. A fuzz target's contract is that it must not
/// crash, hang or corrupt state for any input; the oracle is the process staying alive, so each of
/// these is written to swallow nothing. An exception escaping is the finding. The one exception is a
/// deliberate invariant check where a wrong answer would be worse than a crash — a URL that passes
/// validation and is then not an http URL — which throws so that the fuzzer records it.
/// </para>
/// </remarks>
public static class FuzzTargets
{
    private static readonly ClientClassifier Classifier = new(
        NullGeoIpResolver.Instance,
        Options.Create(new EdgeOptions()));

    /// <summary>Fuzzes the URL and host validation path (T-01, T-02, TC-161 to TC-164).</summary>
    /// <param name="data">Raw bytes from the fuzzer.</param>
    /// <exception cref="InvalidOperationException">
    /// A URL was approved that is not an absolute http or https URL, which is the finding this target
    /// exists to produce.
    /// </exception>
    public static void Url(ReadOnlySpan<byte> data)
    {
        string candidate = Decode(data);

        UrlSafetyVerdict verdict = TargetUrlPolicy.ValidateSyntax(candidate);

        if (verdict.Level == UrlSafetyLevel.Safe)
        {
            // The invariant, checked rather than assumed: an approval must be an absolute web URL.
            // A fuzzer that found an input approving something else has found a T-01 bypass, and a
            // silent wrong answer here would never surface as a crash.
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    "TargetUrlPolicy approved a value that is not an absolute http(s) URL: " + candidate);
            }
        }

        _ = HostNormalizer.TryNormalize(candidate, out string normalized);
        _ = TargetUrlPolicy.IsForbiddenHost(normalized);
        _ = SafeUrl.Web(candidate);
        _ = SafeUrl.Https(candidate);
        _ = SafeUrl.Deeplink(candidate, ReadOnlySpan<string?>.Empty);
        _ = SlugPolicy.TryNormalize(candidate, out _);
    }

    /// <summary>Fuzzes the user-agent classifier (T-10: the key space is attacker controlled).</summary>
    /// <param name="data">Raw bytes from the fuzzer.</param>
    /// <remarks>
    /// The classifier runs compiled regular expressions over a header any client chooses, which is a
    /// denial-of-service primitive without a match timeout — and a timeout is only a mitigation if
    /// nothing else in the path can hang first. This target is where a catastrophic backtracking case
    /// would show up as a hang rather than as a slow afternoon in production.
    /// </remarks>
    public static void UserAgent(ReadOnlySpan<byte> data)
    {
        string candidate = Decode(data);

        ClientRequest request = new()
        {
            Host = "link.example.test",
            Path = "/abc",
            UserAgent = candidate,
            AcceptLanguage = candidate,
            Referrer = candidate,
            SecFetchSite = candidate,
            SecFetchMode = candidate,
            SecFetchDest = candidate,
            Query = ReadOnlyDictionary<string, string>.Empty,
            ReceivedAt = DateTimeOffset.UnixEpoch,
        };

        _ = Classifier.Classify(request);
    }

    /// <summary>Fuzzes the <c>routing_rules</c> deserializer and its validator (T-12).</summary>
    /// <param name="data">Raw bytes from the fuzzer.</param>
    /// <remarks>
    /// T-12 is a deserialization attack on the rule document. The mitigations §E.2.2 names are JSON
    /// Schema validation, depth and size limits, and <c>System.Text.Json</c> without polymorphism;
    /// this target is what checks that the combination has no input it cannot survive. A malformed
    /// document must produce a <see cref="JsonException"/> and nothing else — a
    /// <see cref="StackOverflowException"/> or an <see cref="OutOfMemoryException"/> is not
    /// catchable, which is exactly why the depth cap has to be right rather than merely present.
    /// </remarks>
    public static void RoutingRules(ReadOnlySpan<byte> data)
    {
        string json = Decode(data);

        RoutingRule[]? rules;

        try
        {
            rules = JsonSerializer.Deserialize<RoutingRule[]>(json, DleJson.Default);
        }
        catch (JsonException)
        {
            // The documented failure for a malformed document. Anything else escapes and is a finding.
            return;
        }
        catch (NotSupportedException)
        {
            // A value that cannot be converted to a declared member type. Also documented.
            return;
        }

        _ = RoutingRuleValidator.Validate(rules);
    }

    /// <summary>Fuzzes the Play install referrer parser (T-05).</summary>
    /// <param name="data">Raw bytes from the fuzzer.</param>
    public static void InstallReferrer(ReadOnlySpan<byte> data)
    {
        string candidate = Decode(data);

        _ = Dle.Domain.Attribution.InstallReferrerParser.Parse(candidate);
        _ = Dle.Domain.Attribution.InstallReferrerParser.TryGetClickId(candidate, out _);
    }

    /// <summary>Every target, so a harness can drive them all over the same corpus.</summary>
    public static IReadOnlyList<(string Name, Action<byte[]> Run)> All { get; } =
    [
        ("url", static data => Url(data)),
        ("user-agent", static data => UserAgent(data)),
        ("routing-rules", static data => RoutingRules(data)),
        ("install-referrer", static data => InstallReferrer(data)),
    ];

    /// <summary>
    /// Turns fuzzer bytes into the string the component under test would receive.
    /// </summary>
    /// <remarks>
    /// Lenient rather than strict: a header and a JSON body both arrive as bytes that may not be valid
    /// UTF-8, and a decoder that threw would mean the fuzzer spent its time exploring the decoder
    /// instead of the parser. Replacement characters are exactly what the framework hands a handler in
    /// the same situation.
    /// </remarks>
    private static string Decode(ReadOnlySpan<byte> data) => Encoding.UTF8.GetString(data);
}

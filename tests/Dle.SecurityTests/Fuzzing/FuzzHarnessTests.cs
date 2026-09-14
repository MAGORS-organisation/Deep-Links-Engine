using System.Diagnostics;
using System.Text;

using CsCheck;

namespace Dle.SecurityTests.Fuzzing;

/// <summary>
/// Drives the <see cref="FuzzTargets"/> from a generator, so the parsers are exercised on every build
/// even though SharpFuzz is not available here.
/// </summary>
/// <remarks>
/// <para>
/// This is not a substitute for coverage-guided fuzzing and is not presented as one. libFuzzer follows
/// the branches it discovers; a generator does not, and it will not find the input that takes the one
/// path nobody thought about. What it does do is run the same targets, deterministically, on every
/// build — which catches the regressions a nightly job would find a day late, and keeps the targets
/// compiling and correct so that the nightly job has something to run when it is set up.
/// </para>
/// <para>
/// The seeds matter as much as the generator. Random bytes almost never form a JSON document, so a
/// purely random corpus would test the tokenizer's first character and nothing else. The corpora below
/// therefore mix structured seeds — real documents, real user agents, the open-redirect corpus — with
/// mutations of them and with pure noise.
/// </para>
/// </remarks>
public sealed class FuzzHarnessTests
{
    /// <summary>How long any single input may take before it counts as a hang.</summary>
    /// <remarks>
    /// Generous, because this runs on whatever machine the build is on. It is not a latency budget: it
    /// is the line between "slow" and "this input is a denial of service primitive", and catastrophic
    /// backtracking is orders of magnitude away from it rather than a few milliseconds.
    /// </remarks>
    private static readonly TimeSpan HangThreshold = TimeSpan.FromSeconds(2);

    /// <summary>Documents that are shaped like a rule set, so the parser gets past its first token.</summary>
    private static readonly string[] RoutingRuleSeeds =
    [
        "[]",
        "[{}]",
        """[{"id":"default","then":{"action":"web","url":"https://example.com"}}]""",
        """[{"id":"r1","when":{"platform":["ios"],"os_version":{"gte":"17.0"}},"then":{"action":"app_or_store","store_url":"https://apps.apple.com/app/id1"}},{"id":"default","then":{"action":"web","url":"https://example.com"}}]""",
        """[{"id":"r1","when":{"ab":[{"variant":"a","percent":50},{"variant":"b","percent":50}]},"then":{"action":"web","url":"https://example.com"}}]""",
        """[{"id":"r1","when":{"time_window":{"from":"2026-01-01T00:00:00Z","hours_utc":[9,10,11]}},"then":{"action":"block"}}]""",
        """{"not":"an array"}""",
        """[{"id":"x","then":{"action":"nonsense"}}]""",
        """[{"id":"x","then":{"action":"web","url":null}}]""",
        """[{"id":1,"then":{"action":"web"}}]""",
        "[" + string.Join(",", Enumerable.Repeat("""{"id":"r","then":{"action":"web","url":"https://e.example"}}""", 200)) + "]",
    ];

    /// <summary>Real user agents, plus the shapes that historically break a parser.</summary>
    private static readonly string[] UserAgentSeeds =
    [
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1",
        "Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Instagram 340.0.0.24.109",
        "facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)",
        "Twitterbot/1.0",
        "Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)",
        "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
        "WhatsApp/2.24.1.78 A",
        "",
        " ",
        "(((((((((((((((((((((((((((((((",
        "Mozilla/5.0 " + new string('(', 400),
        "Mozilla/5.0 (" + new string('a', 4000) + ")",
        new string(';', 2000),
    ];

    public static TheoryData<string> TargetNames()
    {
        TheoryData<string> data = new();

        foreach ((string name, _) in FuzzTargets.All)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TargetNames))]
    [Trait("Tool", "fuzz")]
    public void EveryTarget_SurvivesArbitraryBytes(string name)
    {
        // The floor: whatever a socket hands the process, no target may throw anything the caller
        // cannot have expected. This is the case libFuzzer starts from too.
        Action<byte[]> target = FuzzTargets.All.Single(entry => entry.Name == name).Run;

        Gen.Byte.Array[0, 4096].Sample(
            bytes =>
            {
                Run(name, target, bytes);
                return true;
            },
            iter: 5_000);
    }

    [Theory]
    [MemberData(nameof(TargetNames))]
    [Trait("Tool", "fuzz")]
    public void EveryTarget_SurvivesArbitraryText(string name)
    {
        // Text rather than bytes, because a header and a JSON body are text and the interesting
        // failures are in what a parser does with a well-formed but hostile string rather than with
        // noise.
        Action<byte[]> target = FuzzTargets.All.Single(entry => entry.Name == name).Run;

        Gen.String.Sample(
            text =>
            {
                Run(name, target, Encoding.UTF8.GetBytes(text));
                return true;
            },
            iter: 5_000);
    }

    [Fact]
    [Trait("Tool", "fuzz")]
    [Trait("Threat", "T-12")]
    public void RoutingRules_SurvivesMutationsOfRealDocuments()
    {
        // T-12. Random bytes never reach the object graph; a mutated real document does, which is the
        // only way the deserializer's own branches — a wrong member type, a missing required member, a
        // nested object where a scalar was declared — get exercised at all.
        Gen.Select(Gen.Int[0, RoutingRuleSeeds.Length - 1], Gen.Int[0, 4095], Gen.Byte).Sample(
            mutation =>
            {
                (int seed, int position, byte replacement) = mutation;

                byte[] bytes = Encoding.UTF8.GetBytes(RoutingRuleSeeds[seed]);
                bytes[position % bytes.Length] = replacement;

                Run("routing-rules", static data => FuzzTargets.RoutingRules(data), bytes);
                return true;
            },
            iter: 20_000);
    }

    [Fact]
    [Trait("Tool", "fuzz")]
    [Trait("Threat", "T-12")]
    public void RoutingRules_SurvivesDeepAndWideDocuments()
    {
        // The two shapes a depth or size cap exists for. An uncaught StackOverflowException here would
        // not be a test failure — it would end the process — so this is as much a check that the cap
        // is enforced before the recursion as it is a check of the parser.
        Gen.Int[1, 200].Sample(
            depth =>
            {
                string nested = string.Concat(Enumerable.Repeat("""[{"id":"a","then":""", depth))
                    + "{}"
                    + string.Concat(Enumerable.Repeat("}]", depth));

                Run("routing-rules", static data => FuzzTargets.RoutingRules(data), Encoding.UTF8.GetBytes(nested));
                return true;
            },
            iter: 200);
    }

    [Fact]
    [Trait("Tool", "fuzz")]
    [Trait("Threat", "T-10")]
    public void UserAgent_SurvivesMutationsOfRealHeaders()
    {
        // T-10 through the classifier: the header is attacker chosen, the parser is a set of compiled
        // regular expressions, and catastrophic backtracking on a hot path is a denial of service that
        // costs the attacker one request.
        // The hang threshold is about steady-state parsing, not about the one-off cost of the parser
        // compiling its regular-expression set on first use, which on a cold two-vCPU CI runner has
        // been measured above the threshold. Pay that cost once, outside the timed loop.
        FuzzTargets.UserAgent(Encoding.UTF8.GetBytes(UserAgentSeeds[0]));

        Gen.Select(Gen.Int[0, UserAgentSeeds.Length - 1], Gen.Int[0, 8191], Gen.Byte).Sample(
            mutation =>
            {
                (int seed, int position, byte replacement) = mutation;

                byte[] bytes = Encoding.UTF8.GetBytes(UserAgentSeeds[seed]);

                if (bytes.Length > 0)
                {
                    bytes[position % bytes.Length] = replacement;
                }

                Run("user-agent", static data => FuzzTargets.UserAgent(data), bytes);
                return true;
            },
            iter: 5_000);
    }

    [Fact]
    [Trait("Tool", "fuzz")]
    [Trait("Threat", "T-01")]
    public void Url_SurvivesTheWholeOpenRedirectCorpus()
    {
        // The corpus already exists and is exactly the kind of input a URL fuzzer would spend a long
        // time discovering, so it is reused here as a seed set rather than regenerated as noise.
        foreach (string candidate in OpenRedirect.HostileUrlCorpus.Values)
        {
            Run("url", static data => FuzzTargets.Url(data), Encoding.UTF8.GetBytes(candidate));
        }
    }

    [Fact]
    [Trait("Tool", "fuzz")]
    [Trait("Threat", "T-05")]
    public void InstallReferrer_SurvivesPercentEncodingAbuse()
    {
        // The referrer arrives percent-encoded, sometimes twice, from a device the engine does not
        // control. An unescape loop that trusted its input is how a parser turns into an infinite one.
        Gen.String.Sample(
            text =>
            {
                string encoded = Uri.EscapeDataString(text);

                Run(
                    "install-referrer",
                    static data => FuzzTargets.InstallReferrer(data),
                    Encoding.UTF8.GetBytes("dl_cid%3D" + encoded + "%26utm_source%3D" + encoded));

                return true;
            },
            iter: 5_000);
    }

    [Fact]
    [Trait("Tool", "fuzz")]
    public void FuzzTargets_AreAllReachableByName()
    {
        // The targets are the artefact this project ships towards a nightly SharpFuzz job. A target
        // that stopped compiling or was renamed would otherwise be discovered by whoever sets that job
        // up, months later.
        Assert.Equal(4, FuzzTargets.All.Count);
        Assert.Equal(
            FuzzTargets.All.Count,
            FuzzTargets.All.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Runs one target and turns a throw or a hang into a readable failure.</summary>
    private static void Run(string name, Action<byte[]> target, byte[] input)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            target(input);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "The " + name + " fuzz target threw on input " + Describe(input) + ".",
                exception);
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(
            elapsed < HangThreshold,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The {name} fuzz target took {elapsed.TotalMilliseconds:F0} ms on {Describe(input)}, which is the shape of a denial-of-service input rather than a slow one."));
    }

    private static string Describe(byte[] input)
    {
        string hex = Convert.ToHexString(input.AsSpan()[..Math.Min(input.Length, 64)]);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{input.Length} byte(s) starting {hex}");
    }
}

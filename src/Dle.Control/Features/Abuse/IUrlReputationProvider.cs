using Dle.Domain.Abuse;

namespace Dle.Control.Features.Abuse;

/// <summary>
/// One reputation source consulted about a target URL (§E.3 step 3).
/// </summary>
/// <remarks>
/// <para>
/// The seam exists because the licensing of this category is a minefield and the product has to
/// ship something a commercial self-hoster may legally run. URLhaus is free and unrestricted and is
/// therefore the default; Google Safe Browsing v5 is non-commercial only and Web Risk is paid, so
/// both are things a deployment adds behind this interface with its own licence, never something
/// the engine dials out to by default.
/// </para>
/// <para>
/// A provider returns <see langword="null"/> for "no opinion". That is not the same as a clean
/// verdict, and the distinction matters: a feed that is unreachable must not be able to launder a
/// malicious target into an approved one.
/// </para>
/// </remarks>
public interface IUrlReputationProvider
{
    /// <summary>
    /// Source name written into <see cref="UrlSafetyVerdict.Source"/>, for example <c>urlhaus</c>.
    /// </summary>
    string Source { get; }

    /// <summary>Whether the provider is configured and should be consulted.</summary>
    bool IsEnabled { get; }

    /// <summary>Asks the source about one URL.</summary>
    /// <param name="url">The absolute target URL, already validated for syntax.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A verdict, or <see langword="null"/> when the source has nothing to say about this URL or
    /// could not be reached.
    /// </returns>
    ValueTask<UrlSafetyVerdict?> CheckAsync(Uri url, CancellationToken ct);
}

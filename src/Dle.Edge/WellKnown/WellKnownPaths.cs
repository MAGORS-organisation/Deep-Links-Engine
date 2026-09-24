namespace Dle.Edge.WellKnown;

/// <summary>
/// The two request paths that Apple and Google fetch, spelled once.
/// </summary>
/// <remarks>
/// <para>
/// These are not ordinary routes. Apple requires
/// <c>https://&lt;host&gt;/.well-known/apple-app-site-association</c> exactly, with no file extension
/// and no redirect on the way (§A.2.1); Google requires
/// <c>https://&lt;host&gt;/.well-known/assetlinks.json</c> under the same conditions (§A.2.2). Both
/// failures are silent: the file validates, the association simply never forms, and nothing in the
/// application logs says so.
/// </para>
/// <para>
/// The constants exist so that the composition root can exclude the prefix from anything that rewrites
/// or redirects. Concretely, <c>UseHttpsRedirection</c>, <c>UseRewriter</c>, HSTS upgrades and any
/// trailing-slash normalisation must leave <see cref="Prefix"/> alone: a 301 or 307 here is
/// indistinguishable, to Apple, from the file being absent (TC-121, TC-124).
/// </para>
/// </remarks>
public static class WellKnownPaths
{
    /// <summary>The reserved prefix. Nothing in the pipeline may redirect a request below it.</summary>
    public const string Prefix = "/.well-known";

    /// <summary>Apple's association file. No extension: the path is matched verbatim.</summary>
    public const string AppleAppSiteAssociation = Prefix + "/apple-app-site-association";

    /// <summary>Google's Digital Asset Links file.</summary>
    public const string AssetLinks = Prefix + "/assetlinks.json";
}

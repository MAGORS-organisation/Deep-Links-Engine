namespace Dle.Domain.WellKnown;

/// <summary>
/// A generated well-known document, ready to be written to the response.
/// </summary>
/// <param name="Json">The serialized document. It is served verbatim: no reformatting, no BOM.</param>
/// <param name="ETag">
/// Strong validator over <paramref name="Json"/>, already quoted so it can be assigned to the
/// <c>ETag</c> header as-is. It is a content hash, so two nodes generating the same document from the
/// same configuration agree on it without coordination.
/// </param>
/// <param name="ContentType">
/// Always <see cref="JsonContentType"/>. Apple and Google both require it, and neither tolerates a
/// redirect on the way to the file (§A.2.1, §A.2.2, TC-121).
/// </param>
public sealed record WellKnownDocument(string Json, string ETag, string ContentType)
{
    /// <summary>The only content type these documents may be served with.</summary>
    public const string JsonContentType = "application/json";
}

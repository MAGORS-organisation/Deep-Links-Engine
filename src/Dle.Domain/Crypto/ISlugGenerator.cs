namespace Dle.Domain.Crypto;

/// <summary>
/// Turns a database sequence value into a link slug and back: sequence, keyed permutation, then
/// base62 over exactly eight characters (ADR-007).
/// </summary>
/// <remarks>
/// Because the mapping is a bijection, a generated slug never collides and creating a link needs
/// no retry loop against a unique index. The reverse direction is available only to the holder of
/// the key, which makes support questions such as "which link is this?" answerable offline while
/// keeping slugs unguessable to everyone else.
/// </remarks>
public interface ISlugGenerator
{
    /// <summary>Generates the slug for a sequence value.</summary>
    /// <param name="sequenceValue">The next value of the link sequence.</param>
    /// <returns>An eight character base62 slug.</returns>
    string FromSequence(long sequenceValue);

    /// <summary>Recovers the sequence value behind a generated slug.</summary>
    /// <param name="slug">The slug to reverse.</param>
    /// <param name="sequenceValue">The recovered sequence value, or zero on failure.</param>
    /// <returns><see langword="false"/> when the slug is not an eight character base62 string or
    /// does not map back into the sequence space, which is what a custom slug looks like.</returns>
    bool TryRecoverSequence(string slug, out long sequenceValue);
}

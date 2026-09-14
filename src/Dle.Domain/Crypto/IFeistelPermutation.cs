namespace Dle.Domain.Crypto;

/// <summary>
/// A keyed Feistel permutation: a bijection over a fixed width integer space (ADR-007).
/// </summary>
/// <remarks>
/// This is what turns a dense database sequence into a slug that does not leak the order or the
/// volume of link creation, while staying collision free by construction rather than by retrying
/// on a unique index. It is a permutation, not encryption in the confidentiality sense; slug
/// unguessability is a second layer, behind access control and rate limiting (ADR-007).
/// </remarks>
public interface IFeistelPermutation
{
    /// <summary>Width of the permuted space in bits. 47 bits yields eight base62 characters.</summary>
    int Bits { get; }

    /// <summary>Maps a value to its permuted form.</summary>
    /// <param name="value">A value below 2 raised to <see cref="Bits"/>.</param>
    /// <returns>The permuted value, in the same space.</returns>
    ulong Encrypt(ulong value);

    /// <summary>Maps a permuted value back to the original.</summary>
    /// <param name="value">A previously permuted value.</param>
    /// <returns>The original value. Without the key this is not computable.</returns>
    ulong Decrypt(ulong value);
}

using System.Security.Cryptography;
using System.Text;

namespace Dle.UnitTests.Crypto;

/// <summary>
/// Fixed key material and a deterministic generator, so that every crypto test is reproducible.
/// SHARED-KERNEL §0 forbids ambient randomness in a decision; a test that cannot be replayed
/// byte for byte cannot be debugged when it fails once in a thousand runs.
/// </summary>
internal static class CryptoTestKeys
{
    /// <summary>A 32-byte key derived from a constant label; never a random one.</summary>
    internal static byte[] Of(string label) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("dle:unit-test:" + label));

    /// <summary>The primary key used wherever one key is enough.</summary>
    internal static byte[] Primary { get; } = Of("primary");

    /// <summary>A second, unrelated key, used to prove that keys separate outputs.</summary>
    internal static byte[] Secondary { get; } = Of("secondary");

    /// <summary>A master secret long enough for <c>CryptoOptions.MasterSecret</c>.</summary>
    internal const string MasterSecret = "dle-unit-test-master-secret-0123456789abcdef";

    /// <summary>
    /// SplitMix64: a deterministic pseudo-random source with a fixed seed. Used instead of
    /// <see cref="Random"/> so that the sequence is pinned by this file rather than by the runtime.
    /// </summary>
    internal sealed class Sequence
    {
        private ulong _state;

        internal Sequence(ulong seed) => _state = seed;

        internal ulong Next()
        {
            _state += 0x9E3779B97F4A7C15UL;

            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;

            return z ^ (z >> 31);
        }

        /// <summary>Next value in <c>[0, bound]</c> inclusive.</summary>
        internal ulong Next(ulong boundInclusive) =>
            boundInclusive == ulong.MaxValue ? Next() : Next() % (boundInclusive + 1);
    }
}

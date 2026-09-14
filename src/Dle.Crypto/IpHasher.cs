using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Dle.Crypto;

/// <summary>
/// Turns a remote address into the only two forms the engine may store: a keyed hash under a
/// rotating salt, and a truncated network prefix (§B.5.3, FR-247, K9).
/// </summary>
/// <remarks>
/// <para>
/// An IP address is personal data (Breyer, C-582/14), so the raw value never reaches storage.
/// There is deliberately no member on this type that returns, formats or logs the address it was
/// given: the only way a caller can obtain something storable is a hash or a prefix, which makes
/// "we accidentally wrote the raw IP" a compile error rather than a review finding.
/// </para>
/// <para>
/// The salt rotates on a schedule, daily by default. That is what makes the rotation a privacy
/// control rather than key hygiene: once the salt changes, yesterday's hashes can no longer be
/// linked to today's, so the correlation window is bounded by construction instead of by a
/// retention policy someone has to remember to enforce.
/// </para>
/// </remarks>
public sealed class IpHasher : IIpHasher
{
    /// <summary>Longest address the BCL will write, an IPv6 address.</summary>
    private const int MaxAddressBytes = 16;

    /// <summary>Length in bytes of a written IPv4 address.</summary>
    private const int IPv4Bytes = 4;

    private readonly byte[] _secret;
    private readonly long _rotationSeconds;

    /// <summary>
    /// Creates a hasher.
    /// </summary>
    /// <param name="secret">The long lived secret the daily salt is derived from.</param>
    /// <param name="saltRotation">How often the salt changes. Must be positive.</param>
    /// <exception cref="ArgumentException"><paramref name="secret"/> is shorter than 16 bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="saltRotation"/> is not
    /// positive.</exception>
    public IpHasher(ReadOnlySpan<byte> secret, TimeSpan saltRotation)
    {
        if (secret.Length < 16)
        {
            throw new ArgumentException("An IP hash secret must be at least 16 bytes.", nameof(secret));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(saltRotation, TimeSpan.Zero);

        _secret = secret.ToArray();
        _rotationSeconds = (long)saltRotation.TotalSeconds;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The address family is neither IPv4 nor IPv6.</exception>
    public byte[] Hash(IPAddress address, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(address);

        Span<byte> addressBytes = stackalloc byte[MaxAddressBytes];

        if (!TryWriteCanonical(address, addressBytes, out int addressLength))
        {
            throw new ArgumentException("Only IPv4 and IPv6 addresses can be hashed.", nameof(address));
        }

        Span<byte> salt = stackalloc byte[SHA256.HashSizeInBytes];
        DeriveSalt(when, salt);

        byte[] result = new byte[SHA256.HashSizeInBytes];
        HMACSHA256.HashData(salt, addressBytes[..addressLength], result);

        CryptographicOperations.ZeroMemory(salt);
        return result;
    }

    /// <inheritdoc />
    public string? Prefix(IPAddress address)
    {
        if (address is null)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[MaxAddressBytes];

        if (!TryWriteCanonical(address, bytes, out int length))
        {
            return null;
        }

        if (length == IPv4Bytes)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24");
        }

        ushort group0 = BinaryPrimitives.ReadUInt16BigEndian(bytes[..2]);
        ushort group1 = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..4]);
        ushort group2 = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..6]);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{group0:x4}:{group1:x4}:{group2:x4}::/48");
    }

    /// <summary>
    /// Derives the salt of the period an instant falls into. The period index is the message, not
    /// the key, so every period gets an unrelated salt from the same long lived secret and no
    /// separate rotation job has to exist.
    /// </summary>
    private void DeriveSalt(DateTimeOffset when, Span<byte> destination)
    {
        long seconds = when.ToUnixTimeSeconds();
        long period = seconds / _rotationSeconds;

        // Integer division truncates towards zero, which would put the whole second before the
        // Unix epoch into the same period as the second after it.
        if (seconds < 0 && seconds % _rotationSeconds != 0)
        {
            period--;
        }

        Span<byte> message = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(message, period);

        HMACSHA256.HashData(_secret, message, destination);
    }

    /// <summary>
    /// Writes the address in canonical form: an IPv4 mapped IPv6 address becomes the IPv4 address
    /// it stands for, so the same client behind a dual stack proxy hashes to one value rather than
    /// two. Any scope identifier is dropped with the rest of the structure.
    /// </summary>
    private static bool TryWriteCanonical(IPAddress address, Span<byte> destination, out int written)
    {
        IPAddress canonical = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (canonical.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            written = 0;
            return false;
        }

        return canonical.TryWriteBytes(destination, out written);
    }
}

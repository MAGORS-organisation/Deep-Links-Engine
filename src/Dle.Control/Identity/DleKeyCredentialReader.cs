using System.Net;

using Dle.Crypto;

namespace Dle.Control.Identity;

/// <summary>
/// Pulls a key out of a request without deciding anything about it.
/// </summary>
/// <remarks>
/// Shape parsing is not authentication. Everything this class reports is derived from bytes the
/// caller supplied, so a well-formed value still has to survive
/// <see cref="ApiKeyHasher.Verify(string?, ReadOnlySpan{byte})"/> before it means anything.
/// </remarks>
public static class DleKeyCredentialReader
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Reads the raw credential from the <c>Authorization</c> header or from a named header.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <param name="headerName">Header to fall back to, for example <c>X-Api-Key</c>.</param>
    /// <returns>The presented value, or <see langword="null"/> when the request carries none.</returns>
    public static string? Read(HttpRequest request, string headerName)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? authorization = request.Headers.Authorization.ToString();

        if (!string.IsNullOrEmpty(authorization)
            && authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string token = authorization[BearerPrefix.Length..].Trim();

            if (token.Length > 0)
            {
                return token;
            }
        }

        if (!string.IsNullOrWhiteSpace(headerName)
            && request.Headers.TryGetValue(headerName, out Microsoft.Extensions.Primitives.StringValues header))
        {
            string value = header.ToString().Trim();

            if (value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Reports whether a presented value has the shape of a key issued under a given name, and
    /// yields its public prefix.
    /// </summary>
    /// <param name="presentedKey">The value as received. Untrusted.</param>
    /// <param name="keyName">The expected first field, for example <c>dle</c> or <c>dlk</c>.</param>
    /// <param name="prefix">The public prefix on success, empty otherwise.</param>
    /// <returns><see langword="true"/> when the value could be a key of this kind.</returns>
    public static bool TryReadPrefix(string? presentedKey, string keyName, out string prefix)
    {
        prefix = string.Empty;

        if (string.IsNullOrEmpty(presentedKey) || string.IsNullOrEmpty(keyName))
        {
            return false;
        }

        int separator = presentedKey.IndexOf(ApiKeyHasher.FieldSeparator);

        if (separator <= 0
            || !presentedKey.AsSpan(0, separator).Equals(keyName, StringComparison.Ordinal))
        {
            return false;
        }

        return ApiKeyHasher.TryReadPrefix(presentedKey, out prefix);
    }

    /// <summary>
    /// Reduces the caller address to the coarse prefix the §E.9 limiters partition on.
    /// </summary>
    /// <param name="address">The remote address, or <see langword="null"/> when there is none.</param>
    /// <returns>A /24 for IPv4 and a /48 for IPv6, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The full address is never used as a limiter key and is never logged. A single client behind
    /// carrier-grade NAT and a single client on a residential connection should cost the same to
    /// rate limit, and the prefix is enough for that while being far less identifying (§17.5).
    /// </remarks>
    public static string? AddressPrefix(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[16];

        if (!address.TryWriteBytes(bytes, out int written))
        {
            return null;
        }

        return written switch
        {
            4 => string.Create(
                CultureInfo.InvariantCulture,
                $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24"),
            16 => string.Concat(Convert.ToHexStringLower(bytes[..6]), "::/48"),
            _ => null,
        };
    }
}

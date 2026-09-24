using System.ComponentModel.DataAnnotations;

namespace Dle.Crypto;

/// <summary>
/// Cost parameters of the Argon2id hash used for API keys (K5 of the cryptographic inventory).
/// Bound from <c>Dle:Crypto:Argon2</c>.
/// </summary>
/// <remarks>
/// The defaults are the OWASP minimum for Argon2id (19 MiB, two passes, one lane). They are
/// deliberately not higher: an API key is verified on the request path, and the control plane is
/// expected to cache a successful verification for a short time rather than pay the full cost per
/// call. Raising the cost without that cache turns the authentication path into a self inflicted
/// denial of service.
/// </remarks>
public sealed class Argon2Options
{
    /// <summary>Memory cost in kibibytes.</summary>
    [Range(8 * 1024, 4 * 1024 * 1024)]
    public int MemoryKib { get; set; } = 19 * 1024;

    /// <summary>Number of passes over memory.</summary>
    [Range(1, 32)]
    public int Iterations { get; set; } = 2;

    /// <summary>Number of lanes, which bounds the parallelism of an attacker as well as of us.</summary>
    [Range(1, 16)]
    public int Parallelism { get; set; } = 1;

    /// <summary>Length of the derived hash in bytes.</summary>
    [Range(16, 64)]
    public int HashLength { get; set; } = 32;

    /// <summary>Length of the random salt in bytes.</summary>
    [Range(8, 64)]
    public int SaltLength { get; set; } = 16;
}

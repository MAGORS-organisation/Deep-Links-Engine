using Dle.Crypto;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The single composition entry point of the cryptography module (SHARED-KERNEL §15).
/// </summary>
/// <remarks>
/// The namespace is <c>Microsoft.Extensions.DependencyInjection</c> rather than <c>Dle.Crypto</c>,
/// matching every other module in the solution, so that the composition roots of
/// <c>Dle.Edge</c> and <c>Dle.Control</c> can call <see cref="AddDleCrypto"/> without a
/// module-specific <c>using</c> and stay purely declarative (SHARED-KERNEL §15).
/// </remarks>
public static class CryptoServiceCollectionExtensions
{
    /// <summary>
    /// Registers every implementation of the <c>Dle.Domain.Crypto</c> abstractions plus the key
    /// ring, and binds <see cref="CryptoOptions"/> from the <c>Dle:Crypto</c> section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same collection, so calls can be chained.</returns>
    /// <remarks>
    /// <para>
    /// Every seam is registered with <c>TryAdd</c>, so a host that wants a durable
    /// <see cref="ISigningKeyStore"/> or a Valkey backed <see cref="ITokenReplayGuard"/> registers
    /// it before this call and keeps it. The primitives themselves are unconditional: a deployment
    /// that replaced the slug permutation would be issuing slugs that the rest of the engine
    /// cannot reverse.
    /// </para>
    /// <para>
    /// Options are validated at startup, not at first use. A mistyped algorithm or an unparseable
    /// key becomes a refusal to start with a named diagnostic, rather than an exception on the
    /// first request that happens to need signing.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddDleCrypto(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CryptoOptions>()
            .Bind(configuration.GetSection(CryptoOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<CryptoOptions>, CryptoOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);

        // --- Key ring and signing (§E.4.2, ADR-013) ------------------------------------------
        services.TryAddSingleton<ISigningKeyStore, ConfigurationSigningKeyStore>();
        services.TryAddSingleton<IKeyRing, KeyRing>();

        // Transient factories rather than singletons: the ring swaps its snapshot on rotation, so
        // a captured signer would keep signing with a key that is no longer current.
        services.TryAddTransient(static sp => sp.GetRequiredService<IKeyRing>().CurrentSigner);
        services.TryAddTransient(static sp => sp.GetRequiredService<IKeyRing>().Verifier);

        // --- Tokens (§E.4.2) ------------------------------------------------------------------
        services.TryAddSingleton<ITokenReplayGuard>(static sp => new InMemoryTokenReplayGuard(
            sp.GetRequiredService<IOptions<CryptoOptions>>().Value.ReplayGuardCapacity,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<InMemoryTokenReplayGuard>>()));

        services.TryAddSingleton<SignedTokenCodec>();

        // --- Slugs (ADR-007) ------------------------------------------------------------------
        services.TryAddSingleton<IFeistelPermutation>(static sp =>
        {
            CryptoOptions options = sp.GetRequiredService<IOptions<CryptoOptions>>().Value;

            return new FeistelPermutation(
                CryptoKeyDerivation.Derive(options.MasterSecret, options.SlugSecret, CryptoKeyDerivation.SlugLabel),
                FeistelPermutation.SlugBits,
                options.SlugFeistelRounds);
        });

        services.TryAddSingleton<ISlugGenerator, SlugGenerator>();

        // --- Click identifiers (§B.6.3) -------------------------------------------------------
        services.TryAddSingleton<IClickIdCodec>(static sp =>
        {
            CryptoOptions options = sp.GetRequiredService<IOptions<CryptoOptions>>().Value;

            return new ClickIdCodec(
                CryptoKeyDerivation.Derive(options.MasterSecret, options.ClickIdSecret, CryptoKeyDerivation.ClickIdPermutationLabel),
                CryptoKeyDerivation.Derive(options.MasterSecret, options.ClickIdSecret, CryptoKeyDerivation.ClickIdMacLabel),
                options.SlugFeistelRounds);
        });

        // --- Privacy (§B.5.3, FR-247, K9) -----------------------------------------------------
        services.TryAddSingleton<IIpHasher>(static sp =>
        {
            CryptoOptions options = sp.GetRequiredService<IOptions<CryptoOptions>>().Value;

            return new IpHasher(
                CryptoKeyDerivation.Derive(options.MasterSecret, options.IpHashSecret, CryptoKeyDerivation.IpHashLabel),
                TimeSpan.FromHours(options.IpSaltRotationHours));
        });

        // --- Credentials (K3, K5) -------------------------------------------------------------
        services.TryAddSingleton(static sp =>
            new Argon2PasswordHasher(sp.GetRequiredService<IOptions<CryptoOptions>>().Value.Argon2));

        services.TryAddSingleton<ApiKeyHasher>();

        services.TryAddSingleton(static sp =>
        {
            CryptoOptions options = sp.GetRequiredService<IOptions<CryptoOptions>>().Value;

            return new ClaimCodeGenerator(
                CryptoKeyDerivation.Derive(options.MasterSecret, options.ClaimCodeSecret, CryptoKeyDerivation.ClaimCodeLabel));
        });

        return services;
    }
}

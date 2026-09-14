using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Options;

namespace Dle.Crypto;

/// <summary>
/// Cross field validation of <see cref="CryptoOptions"/>, run at startup alongside the data
/// annotations.
/// </summary>
/// <remarks>
/// Data annotations can say that a field is present and inside a range; they cannot say that the
/// configured algorithm has an implementation, that a post-quantum algorithm was actually enabled,
/// or that a configured key parses. Those are exactly the mistakes that would otherwise surface as
/// a failure to sign under load, long after the deployment looked healthy, so they are turned into
/// a refusal to start.
/// </remarks>
public sealed class CryptoOptionsValidator : IValidateOptions<CryptoOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, CryptoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        ValidateSigningAlgorithm(options, failures);
        ValidateAcceptedAlgorithms(options, failures);
        ValidateArgon2(options, failures);
        ValidateKeys(options, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateSigningAlgorithm(CryptoOptions options, List<string> failures)
    {
        if (!SigningKeyFactory.IsSupported(options.SigningAlgorithm))
        {
            failures.Add(
                "Dle:Crypto:SigningAlgorithm is '" + options.SigningAlgorithm +
                "', which this provider does not implement. Supported: " +
                string.Join(", ", SigningKeyFactory.SupportedAlgorithms) + ".");
            return;
        }

        if (SigningKeyFactory.IsPostQuantum(options.SigningAlgorithm) && !options.HybridPqEnabled)
        {
            failures.Add(
                "Dle:Crypto:SigningAlgorithm is '" + options.SigningAlgorithm +
                "', which is a post-quantum algorithm. Set Dle:Crypto:HybridPqEnabled to true to enable it; " +
                "docs/zadanie.md section E.5.3 keeps these off in phase 0 on purpose.");
        }

        bool needsGeneratedKey = string.Equals(
            options.SigningAlgorithm,
            SignatureAlgorithms.SlhDsa128s,
            StringComparison.Ordinal);

        if (needsGeneratedKey && !HasConfiguredKeyFor(options, options.SigningAlgorithm))
        {
            failures.Add(
                "Dle:Crypto:SigningAlgorithm is 'SLHDSA128s', whose private key cannot be derived from the master " +
                "secret. Configure a generated key for it under Dle:Crypto:Keys.");
        }
    }

    private static void ValidateAcceptedAlgorithms(CryptoOptions options, List<string> failures)
    {
        foreach (string algorithm in options.AcceptedAlgorithms)
        {
            if (!SigningKeyFactory.IsSupported(algorithm))
            {
                failures.Add(
                    "Dle:Crypto:AcceptedAlgorithms contains '" + algorithm +
                    "', which this provider does not implement.");
            }
            else if (SigningKeyFactory.IsPostQuantum(algorithm) && !options.HybridPqEnabled)
            {
                failures.Add(
                    "Dle:Crypto:AcceptedAlgorithms contains the post-quantum algorithm '" + algorithm +
                    "' while Dle:Crypto:HybridPqEnabled is false.");
            }
        }
    }

    private static void ValidateArgon2(CryptoOptions options, List<string> failures)
    {
        List<ValidationResult> results = [];

        if (!Validator.TryValidateObject(options.Argon2, new ValidationContext(options.Argon2), results, validateAllProperties: true))
        {
            foreach (ValidationResult result in results)
            {
                failures.Add("Dle:Crypto:Argon2 " + (result.ErrorMessage ?? "is invalid."));
            }
        }
    }

    private static void ValidateKeys(CryptoOptions options, List<string> failures)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (SigningKeyOptions key in options.Keys)
        {
            List<ValidationResult> results = [];

            if (!Validator.TryValidateObject(key, new ValidationContext(key), results, validateAllProperties: true))
            {
                foreach (ValidationResult result in results)
                {
                    failures.Add("Dle:Crypto:Keys entry '" + key.Kid + "': " + (result.ErrorMessage ?? "is invalid."));
                }

                continue;
            }

            if (!seen.Add(key.Kid))
            {
                failures.Add("Dle:Crypto:Keys contains more than one key with kid '" + key.Kid + "'.");
                continue;
            }

            if (!SigningKeyFactory.IsSupported(key.Algorithm))
            {
                failures.Add(
                    "Dle:Crypto:Keys entry '" + key.Kid + "' uses algorithm '" + key.Algorithm +
                    "', which this provider does not implement.");
                continue;
            }

            if (SigningKeyFactory.IsPostQuantum(key.Algorithm) && !options.HybridPqEnabled)
            {
                failures.Add(
                    "Dle:Crypto:Keys entry '" + key.Kid + "' uses the post-quantum algorithm '" + key.Algorithm +
                    "' while Dle:Crypto:HybridPqEnabled is false.");
                continue;
            }

            if (key.NotBefore is not null && key.NotAfter is not null && key.NotAfter <= key.NotBefore)
            {
                failures.Add("Dle:Crypto:Keys entry '" + key.Kid + "' has NotAfter at or before NotBefore.");
                continue;
            }

            // Parsing here rather than at first use turns a mistyped key into a failed start with
            // a named diagnostic, instead of an exception on the first request that needs it.
            try
            {
                _ = SigningKeyFactory.FromOptions(key);
            }
            catch (InvalidOperationException e)
            {
                failures.Add("Dle:Crypto:Keys entry '" + key.Kid + "': " + e.Message);
            }
        }
    }

    private static bool HasConfiguredKeyFor(CryptoOptions options, string algorithmId)
    {
        foreach (SigningKeyOptions key in options.Keys)
        {
            if (string.Equals(key.Algorithm, algorithmId, StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(key.PrivateKey))
            {
                return true;
            }
        }

        return false;
    }
}

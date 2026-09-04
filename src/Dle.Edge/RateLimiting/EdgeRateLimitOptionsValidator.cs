using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Options;

namespace Dle.Edge.RateLimiting;

/// <summary>
/// Validates <see cref="EdgeRateLimitOptions"/> and each of its three nested sections at start-up.
/// </summary>
/// <remarks>
/// <para>
/// <c>ValidateDataAnnotations</c> checks the object it is registered for and does not descend into a
/// nested one, so binding the whole of <c>Dle:RateLimits:Edge</c> as a single tree would validate the
/// one boolean on the parent and nothing else. A window of zero seconds or a bucket of zero tokens
/// would then bind cleanly and take the rate limiter out on the first request — which for the 404
/// budget means the enumeration defence of T-07 is silently absent.
/// </para>
/// <para>
/// The alternative, binding each subsection as its own options type, would give the same coverage and
/// then leave two objects describing one section: the nested instance the parent already carries, and
/// the separately bound one. Validating the tree in one place keeps the parent as the single source
/// the limiter reads from.
/// </para>
/// </remarks>
public sealed class EdgeRateLimitOptionsValidator : IValidateOptions<EdgeRateLimitOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, EdgeRateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string>? failures = null;

        Check(options, EdgeRateLimitOptions.SectionName, ref failures);
        Check(options.Resolve, ResolveRateLimitOptions.SectionName, ref failures);
        Check(options.NotFound, NotFoundRateLimitOptions.SectionName, ref failures);
        Check(options.Qr, QrRateLimitOptions.SectionName, ref failures);

        // A burst smaller than the replenishment rate is not a range error on any single property, but
        // it describes a bucket that can never hold one period's worth of tokens. Saying so at start-up
        // beats discovering it as an unexplained 429 under normal load.
        if (options.NotFound.Burst < options.NotFound.TokensPerPeriod)
        {
            (failures ??= []).Add(
                $"{NotFoundRateLimitOptions.SectionName}: Burst must be at least TokensPerPeriod, " +
                $"otherwise the bucket cannot hold one period of replenishment.");
        }

        return failures is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(object instance, string section, ref List<string>? failures)
    {
        var results = new List<ValidationResult>();

        if (Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true))
        {
            return;
        }

        failures ??= [];

        foreach (ValidationResult result in results)
        {
            failures.Add($"{section}: {result.ErrorMessage}");
        }
    }
}

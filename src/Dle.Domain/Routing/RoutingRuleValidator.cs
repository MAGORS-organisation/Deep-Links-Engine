namespace Dle.Domain.Routing;

/// <summary>
/// Write-time validation of a rule set (FR-127, TC-105).
/// </summary>
/// <remarks>
/// <para>
/// This runs in the control plane before a link is stored, never on the resolve hot path. Its purpose
/// is to make the edge's job total: once a rule set has passed, the engine can assume there is exactly
/// one default rule, that every action has the URLs it needs, and that no URL can carry a scheme other
/// than <c>http</c> or <c>https</c>.
/// </para>
/// <para>
/// The rejection of a rule set without a default rule is the important one. Without it, a client that
/// matches nothing would silently receive <c>404</c> for a link the marketer believes is live — the
/// single most common way a hand-written rule set fails in production (TC-105).
/// </para>
/// </remarks>
public static class RoutingRuleValidator
{
    /// <summary>Maximum number of rules per link. Beyond this the evaluation cost stops being predictable.</summary>
    public const int MaxRules = 50;

    /// <summary>Maximum serialized size of the rule document, enforced by the API layer before parsing.</summary>
    public const int MaxJsonBytes = 64 * 1024;

    private static readonly IReadOnlyList<RoutingValidationError> NoErrors = [];

    /// <summary>
    /// Validates a rule set and returns every problem found, rather than stopping at the first one, so
    /// the API can answer with a complete RFC 9457 problem document.
    /// </summary>
    /// <param name="rules">The rule set to check. <see langword="null"/> and empty are both invalid.</param>
    /// <returns>An empty list when the rule set may be stored; otherwise one entry per problem.</returns>
    public static IReadOnlyList<RoutingValidationError> Validate(IReadOnlyList<RoutingRule>? rules)
    {
        var errors = new List<RoutingValidationError>();

        if (rules is null || rules.Count == 0)
        {
            errors.Add(new RoutingValidationError(
                "rules",
                "A link needs at least one rule, and one of them must be the default rule (a rule without \"when\")."));

            return errors;
        }

        if (rules.Count > MaxRules)
        {
            errors.Add(new RoutingValidationError(
                "rules",
                FormattableString.Invariant($"A link may declare at most {MaxRules} rules; {rules.Count} were supplied.")));
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        int defaultCount = 0;

        for (int i = 0; i < rules.Count; i++)
        {
            RoutingRule rule = rules[i];

            if (rule is null)
            {
                errors.Add(new RoutingValidationError(RulePath(i, string.Empty), "The rule is null."));
                continue;
            }

            ValidateId(rule, i, seenIds, errors);

            if (rule.When is null)
            {
                defaultCount++;

                if (i != rules.Count - 1)
                {
                    errors.Add(new RoutingValidationError(
                        RulePath(i, string.Empty),
                        "The default rule (a rule without \"when\") must be the last rule; rules after it can never match."));
                }
            }
            else
            {
                ValidateCondition(rule.When, i, errors);
            }

            ValidateAction(rule.Then, i, errors);
        }

        if (defaultCount == 0)
        {
            errors.Add(new RoutingValidationError(
                "rules",
                "A default rule is required: exactly one rule must omit \"when\" so that every client resolves to a target."));
        }
        else if (defaultCount > 1)
        {
            errors.Add(new RoutingValidationError(
                "rules",
                FormattableString.Invariant($"Exactly one default rule is allowed; {defaultCount} rules omit \"when\".")));
        }

        return errors.Count == 0 ? NoErrors : errors;
    }

    /// <summary>Convenience wrapper around <see cref="Validate"/>.</summary>
    /// <param name="rules">The rule set to check.</param>
    /// <returns><see langword="true"/> when the rule set may be stored.</returns>
    public static bool IsValid(IReadOnlyList<RoutingRule>? rules) => Validate(rules).Count == 0;

    private static void ValidateId(RoutingRule rule, int index, HashSet<string> seenIds, List<RoutingValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(rule.Id))
        {
            errors.Add(new RoutingValidationError(
                RulePath(index, ".id"),
                "The rule id is required; it is written to the click stream and identifies the rule in reports."));

            return;
        }

        if (!seenIds.Add(rule.Id))
        {
            errors.Add(new RoutingValidationError(
                RulePath(index, ".id"),
                FormattableString.Invariant($"Duplicate rule id \"{rule.Id}\"; ids must be unique within a link.")));
        }
    }

    private static void ValidateAction(RuleAction? action, int index, List<RoutingValidationError> errors)
    {
        if (action is null)
        {
            errors.Add(new RoutingValidationError(RulePath(index, ".then"), "The \"then\" action is required."));
            return;
        }

        if (!Enum.IsDefined(action.Action))
        {
            errors.Add(new RoutingValidationError(
                RulePath(index, ".then.action"),
                "Unknown action. Allowed: web, app_or_store, store_only, app_only, block."));
        }

        if (!Enum.IsDefined(action.Interstitial))
        {
            errors.Add(new RoutingValidationError(
                RulePath(index, ".then.interstitial"),
                "Unknown interstitial mode. Allowed: auto, always, never."));
        }

        if (action.Action == RoutingActionKind.Web && string.IsNullOrWhiteSpace(action.Url))
        {
            errors.Add(new RoutingValidationError(
                RulePath(index, ".then.url"),
                "The \"web\" action requires an absolute http or https url."));
        }

        if (action.Action is RoutingActionKind.AppOrStore or RoutingActionKind.StoreOnly
            && string.IsNullOrWhiteSpace(action.StoreUrl))
        {
            errors.Add(new RoutingValidationError(
                RulePath(index, ".then.store_url"),
                "The \"app_or_store\" and \"store_only\" actions require an absolute http or https store_url."));
        }

        ValidateUrl(action.Url, RulePath(index, ".then.url"), errors);
        ValidateUrl(action.StoreUrl, RulePath(index, ".then.store_url"), errors);
        ValidateDeeplinkPath(action.DeeplinkPath, index, errors);
    }

    private static void ValidateUrl(string? url, string path, List<RoutingValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            errors.Add(new RoutingValidationError(path, "The url must be absolute, including the scheme and host."));
            return;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            errors.Add(new RoutingValidationError(
                path,
                FormattableString.Invariant($"Scheme \"{uri.Scheme}\" is not allowed; only http and https may be used as a redirect target.")));
        }
    }

    /// <summary>
    /// A deep link path is a path, never a URL. Accepting an absolute or protocol-relative value here
    /// would turn <c>deeplink_path</c> into an open redirect that bypasses the target URL policy (T-01).
    /// </summary>
    private static void ValidateDeeplinkPath(string? deeplinkPath, int index, List<RoutingValidationError> errors)
    {
        if (string.IsNullOrEmpty(deeplinkPath))
        {
            return;
        }

        bool looksAbsolute =
            deeplinkPath.Contains("://", StringComparison.Ordinal)
            || deeplinkPath.StartsWith("//", StringComparison.Ordinal)
            || deeplinkPath.StartsWith("\\\\", StringComparison.Ordinal);

        if (looksAbsolute)
        {
            errors.Add(new RoutingValidationError(
                RulePath(index, ".then.deeplink_path"),
                "The deeplink path must be a path such as \"/product/123\", not an absolute or protocol-relative URL."));
        }
    }

    private static void ValidateCondition(RuleCondition when, int index, List<RoutingValidationError> errors)
    {
        ValidateAb(when.Ab, index, errors);
        ValidateTimeWindow(when.TimeWindow, index, errors);
    }

    private static void ValidateAb(AbVariant[]? variants, int index, List<RoutingValidationError> errors)
    {
        if (variants is null || variants.Length == 0)
        {
            return;
        }

        var seenVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int total = 0;

        for (int v = 0; v < variants.Length; v++)
        {
            AbVariant variant = variants[v];

            if (variant is null)
            {
                errors.Add(new RoutingValidationError(RulePath(index, FormattableString.Invariant($".when.ab[{v}]")), "The variant is null."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(variant.Variant))
            {
                errors.Add(new RoutingValidationError(
                    RulePath(index, FormattableString.Invariant($".when.ab[{v}].variant")),
                    "The variant name is required; it is reported in analytics."));
            }
            else if (!seenVariants.Add(variant.Variant.Trim()))
            {
                errors.Add(new RoutingValidationError(
                    RulePath(index, FormattableString.Invariant($".when.ab[{v}].variant")),
                    FormattableString.Invariant($"Duplicate variant name \"{variant.Variant}\"; names must be unique within a rule.")));
            }

            if (variant.Percent is < 1 or > 100)
            {
                errors.Add(new RoutingValidationError(
                    RulePath(index, FormattableString.Invariant($".when.ab[{v}].percent")),
                    FormattableString.Invariant($"Percent must be between 1 and 100; {variant.Percent} was supplied.")));
            }
            else
            {
                total += variant.Percent;
            }
        }

        if (total > 100)
        {
            errors.Add(new RoutingValidationError(
                RulePath(index, ".when.ab"),
                FormattableString.Invariant($"The variant percentages sum to {total}; the sum must not exceed 100.")));
        }
    }

    private static void ValidateTimeWindow(TimeWindowPredicate? window, int index, List<RoutingValidationError> errors)
    {
        if (window is null)
        {
            return;
        }

        if (window.From is { } from && window.To is { } to && to <= from)
        {
            errors.Add(new RoutingValidationError(
                RulePath(index, ".when.time_window.to"),
                "The end of the window must be later than its start."));
        }

        if (window.HoursUtc is { } hours)
        {
            for (int h = 0; h < hours.Length; h++)
            {
                if (hours[h] is < 0 or > 23)
                {
                    errors.Add(new RoutingValidationError(
                        RulePath(index, FormattableString.Invariant($".when.time_window.hours_utc[{h}]")),
                        FormattableString.Invariant($"An hour must be between 0 and 23; {hours[h]} was supplied.")));
                }
            }
        }

        if (window.DaysOfWeekUtc is { } days)
        {
            for (int d = 0; d < days.Length; d++)
            {
                if (days[d] is < 0 or > 6)
                {
                    errors.Add(new RoutingValidationError(
                        RulePath(index, FormattableString.Invariant($".when.time_window.days_of_week_utc[{d}]")),
                        FormattableString.Invariant($"A day must be between 0 (Sunday) and 6 (Saturday); {days[d]} was supplied.")));
                }
            }
        }
    }

    private static string RulePath(int index, string suffix) =>
        string.Create(CultureInfo.InvariantCulture, $"rules[{index}]{suffix}");
}

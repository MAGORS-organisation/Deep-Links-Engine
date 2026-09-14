namespace Dle.Control.Features.Links;

/// <summary>
/// The sentences the rule simulator explains itself with, in English and Slovak (NFR-15, FR-129).
/// </summary>
/// <remarks>
/// <para>
/// The simulator answers a human question — "why did rule 2 lose?" — so its output is prose and
/// therefore user-facing text, which NFR-15 requires in both languages. The alternative, a machine
/// readable diff the UI would have to render into sentences, moves the same translation problem
/// into the front end and duplicates it per client.
/// </para>
/// <para>
/// The templates are positional rather than named, and are formatted with
/// <see cref="CultureInfo.InvariantCulture"/>: the values interpolated into them are identifiers and
/// codes — a platform name, a country code, a rule identifier — not numbers or dates whose spelling
/// should follow a locale.
/// </para>
/// </remarks>
internal sealed class SimulationVocabulary
{
    /// <summary>English, the fallback for every language the engine does not ship.</summary>
    internal static SimulationVocabulary English { get; } = new()
    {
        Language = "en",
        Matched = "Rule {0} \"{1}\" matched: every condition held.",
        DefaultMatched = "Rule {0} \"{1}\" is the default rule, so it matches every client.",
        NotEvaluated = "Rule {0} \"{1}\" was not evaluated because rule {2} had already matched.",
        NotMatched = "Rule {0} \"{1}\" did not match: {2}.",
        NoRules = "This link has no routing rules, so nothing can match and the link answers 404.",
        NoMatch = "No rule matched. A link without a default rule cannot happen through this API, so "
            + "this rule set was written directly to the database.",
        ReasonPlatform = "the client is {0} and the rule requires {1}",
        ReasonCountry = "the client is in {0} and the rule requires {1}",
        ReasonRegion = "the client region is {0} and the rule requires {1}",
        ReasonLanguage = "the client language is {0} and the rule requires {1}",
        ReasonChannel = "the client arrived through {0} and the rule requires {1}",
        ReasonOsVersion = "the operating system version {0} is outside the range the rule requires",
        ReasonAppVersion = "the application version {0} is outside the range the rule requires",
        ReasonTimeWindow = "{0} is outside the rule's time window",
        ReasonAbBucket = "the A/B bucket {0} falls outside the share this rule takes",
        Unknown = "unknown",
        AbSelected = "A/B variant \"{0}\" was selected for bucket {1}.",
    };

    /// <summary>Slovak, the deployment language of the first customers (NFR-15).</summary>
    internal static SimulationVocabulary Slovak { get; } = new()
    {
        Language = "sk",
        Matched = "Pravidlo {0} „{1}“ sa zhodovalo: všetky podmienky platia.",
        DefaultMatched = "Pravidlo {0} „{1}“ je predvolené, preto sa zhoduje s každým klientom.",
        NotEvaluated = "Pravidlo {0} „{1}“ sa nevyhodnocovalo, pretože už sa zhodovalo pravidlo {2}.",
        NotMatched = "Pravidlo {0} „{1}“ sa nezhodovalo: {2}.",
        NoRules = "Tento link nemá žiadne pravidlá, takže sa nič nemôže zhodovať a link odpovie 404.",
        NoMatch = "Nezhodovalo sa žiadne pravidlo. Sada bez predvoleného pravidla cez toto API "
            + "neprejde, takže bola zapísaná priamo do databázy.",
        ReasonPlatform = "klient je {0} a pravidlo vyžaduje {1}",
        ReasonCountry = "klient je v {0} a pravidlo vyžaduje {1}",
        ReasonRegion = "región klienta je {0} a pravidlo vyžaduje {1}",
        ReasonLanguage = "jazyk klienta je {0} a pravidlo vyžaduje {1}",
        ReasonChannel = "klient prišiel cez {0} a pravidlo vyžaduje {1}",
        ReasonOsVersion = "verzia operačného systému {0} je mimo rozsahu, ktorý pravidlo vyžaduje",
        ReasonAppVersion = "verzia aplikácie {0} je mimo rozsahu, ktorý pravidlo vyžaduje",
        ReasonTimeWindow = "{0} je mimo časového okna pravidla",
        ReasonAbBucket = "A/B bucket {0} je mimo podielu, ktorý si pravidlo berie",
        Unknown = "neznáme",
        AbSelected = "Pre bucket {1} bol vybraný A/B variant „{0}“.",
    };

    /// <summary>The language tag this vocabulary is written in.</summary>
    public required string Language { get; init; }

    /// <summary>Template for a rule whose conditions all held.</summary>
    public required string Matched { get; init; }

    /// <summary>Template for the default rule when it is the one that matched.</summary>
    public required string DefaultMatched { get; init; }

    /// <summary>Template for a rule that came after the winner.</summary>
    public required string NotEvaluated { get; init; }

    /// <summary>Template for a rule that was evaluated and lost.</summary>
    public required string NotMatched { get; init; }

    /// <summary>Sentence used when the link declares no rules at all.</summary>
    public required string NoRules { get; init; }

    /// <summary>Sentence used when no rule matched and there was no default rule.</summary>
    public required string NoMatch { get; init; }

    /// <summary>Reason template for a platform mismatch.</summary>
    public required string ReasonPlatform { get; init; }

    /// <summary>Reason template for a country mismatch.</summary>
    public required string ReasonCountry { get; init; }

    /// <summary>Reason template for a region mismatch.</summary>
    public required string ReasonRegion { get; init; }

    /// <summary>Reason template for a language mismatch.</summary>
    public required string ReasonLanguage { get; init; }

    /// <summary>Reason template for a channel mismatch.</summary>
    public required string ReasonChannel { get; init; }

    /// <summary>Reason template for an operating system version outside the predicate.</summary>
    public required string ReasonOsVersion { get; init; }

    /// <summary>Reason template for an application version outside the predicate.</summary>
    public required string ReasonAppVersion { get; init; }

    /// <summary>Reason template for an instant outside the rule's time window.</summary>
    public required string ReasonTimeWindow { get; init; }

    /// <summary>Reason template for an A/B bucket outside the rule's share.</summary>
    public required string ReasonAbBucket { get; init; }

    /// <summary>Word used where the client supplied no value for a dimension.</summary>
    public required string Unknown { get; init; }

    /// <summary>Template announcing which A/B variant the bucket selected.</summary>
    public required string AbSelected { get; init; }

    /// <summary>
    /// Picks the vocabulary for a request, from the <c>Accept-Language</c> header.
    /// </summary>
    /// <param name="acceptLanguage">The header value, or <see langword="null"/>.</param>
    /// <returns>The Slovak vocabulary when Slovak is preferred, otherwise the English one.</returns>
    /// <remarks>
    /// Deliberately crude: the header is scanned for a Slovak subtag and anything else falls back to
    /// English. Full quality-value negotiation would be the right answer for a page served to the
    /// public; this is an operator tool with two languages, and the header is a hint rather than a
    /// contract.
    /// </remarks>
    internal static SimulationVocabulary ForRequest(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
        {
            return English;
        }

        foreach (string part in acceptLanguage.Split(','))
        {
            string tag = part.Split(';')[0].Trim();

            if (tag.StartsWith("sk", StringComparison.OrdinalIgnoreCase))
            {
                return Slovak;
            }
        }

        return English;
    }

    /// <summary>Formats a template with invariant culture.</summary>
    /// <param name="template">One of the templates on this vocabulary.</param>
    /// <param name="arguments">The values to interpolate.</param>
    /// <returns>The formatted sentence.</returns>
    internal static string Format(string template, params object?[] arguments) =>
        string.Format(CultureInfo.InvariantCulture, template, arguments);
}

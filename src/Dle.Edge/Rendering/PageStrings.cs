using Dle.Domain.Clients;
using Dle.Domain.Ports;

namespace Dle.Edge.Rendering;

/// <summary>
/// Every user-facing string on every page the edge renders, in the two languages the product ships
/// (NFR-15).
/// </summary>
/// <remarks>
/// <para>
/// The strings are compiled constants rather than resource lookups. The pages are on the resolve path,
/// and a satellite assembly probe plus a <c>ResourceManager</c> lookup per string is measurable there;
/// more importantly, <c>InvariantGlobalization</c> is on for the whole solution, so culture-driven
/// resource fallback would not behave the way it appears to. Adding a language means adding a static
/// instance and a case to <see cref="For(string?, DomainRuntimeConfig?, InterstitialOptions)"/>.
/// </para>
/// <para>
/// The copy avoids the second person imperative where the user has no agency ("this link is not
/// available", not "you cannot open this link") and never names the reason a link is missing: the 404
/// text has to read identically whether the slug does not exist or belongs to another tenant
/// (TC-166, SHARED-KERNEL §17.7).
/// </para>
/// </remarks>
internal sealed record PageStrings
{
    /// <summary>BCP 47 tag written into the <c>lang</c> attribute.</summary>
    internal required string LanguageTag { get; init; }

    /// <summary>Title of the interstitial document.</summary>
    internal required string InterstitialDocumentTitle { get; init; }

    /// <summary>Heading of the interstitial.</summary>
    internal required string InterstitialHeading { get; init; }

    /// <summary>Explanatory sentence under the interstitial heading.</summary>
    internal required string InterstitialLede { get; init; }

    /// <summary>Label of the primary anchor: the one the user has to tap for the app to open.</summary>
    internal required string OpenInApp { get; init; }

    /// <summary>Label of the fallback anchor when it points at the App Store.</summary>
    internal required string ContinueToAppStore { get; init; }

    /// <summary>Label of the fallback anchor when it points at Google Play.</summary>
    internal required string ContinueToGooglePlay { get; init; }

    /// <summary>Label of the fallback anchor when it points at an unidentified store.</summary>
    internal required string ContinueToStore { get; init; }

    /// <summary>Label of the fallback anchor when it points at the web target.</summary>
    internal required string ContinueToWebsite { get; init; }

    /// <summary>Label above the destination the page will send the user to.</summary>
    internal required string DestinationLabel { get; init; }

    /// <summary>Status text while the automatic navigation is pending.</summary>
    internal required string CountdownRunning { get; init; }

    /// <summary>Status text after the user stops the automatic navigation.</summary>
    internal required string CountdownStopped { get; init; }

    /// <summary>Label of the button that stops the automatic navigation (WCAG 2.2 SC 2.2.1).</summary>
    internal required string StopAutoRedirect { get; init; }

    /// <summary>Heading of the claim code block.</summary>
    internal required string ClaimCodeHeading { get; init; }

    /// <summary>Instruction above the claim code.</summary>
    internal required string ClaimCodeLede { get; init; }

    /// <summary>Note about the claim code's lifetime.</summary>
    internal required string ClaimCodeNote { get; init; }

    /// <summary>Accessible name prefix read before the claim code's characters.</summary>
    internal required string ClaimCodeAccessiblePrefix { get; init; }

    /// <summary>Title of the 404 document.</summary>
    internal required string NotFoundDocumentTitle { get; init; }

    /// <summary>Heading of the 404 page.</summary>
    internal required string NotFoundHeading { get; init; }

    /// <summary>First paragraph of the 404 page.</summary>
    internal required string NotFoundBody { get; init; }

    /// <summary>Second paragraph of the 404 page.</summary>
    internal required string NotFoundHint { get; init; }

    /// <summary>Title of the 410 document.</summary>
    internal required string GoneDocumentTitle { get; init; }

    /// <summary>Heading of the 410 page.</summary>
    internal required string GoneHeading { get; init; }

    /// <summary>What happened to the link.</summary>
    internal required string GoneBody { get; init; }

    /// <summary>Why the record is kept rather than deleted.</summary>
    internal required string GoneRetention { get; init; }

    /// <summary>Heading of the appeal block.</summary>
    internal required string GoneAppealHeading { get; init; }

    /// <summary>How to appeal.</summary>
    internal required string GoneAppealBody { get; init; }

    /// <summary>Label of the appeal form anchor.</summary>
    internal required string GoneAppealFormLabel { get; init; }

    /// <summary>Label of the appeal mailbox anchor.</summary>
    internal required string GoneAppealEmailLabel { get; init; }

    /// <summary>Fallback sentence when the operator configured no appeal channel.</summary>
    internal required string GoneAppealUnconfigured { get; init; }

    /// <summary>Legal basis of the notice and action mechanism.</summary>
    internal required string GoneLegalNote { get; init; }

    /// <summary>Title of the 429 document.</summary>
    internal required string TooManyDocumentTitle { get; init; }

    /// <summary>Heading of the 429 page.</summary>
    internal required string TooManyHeading { get; init; }

    /// <summary>Body of the 429 page.</summary>
    internal required string TooManyBody { get; init; }

    /// <summary>Retry hint with a concrete number of seconds. Contains one <c>{0}</c> placeholder.</summary>
    internal required string TooManyRetryAfterFormat { get; init; }

    /// <summary>Retry hint without a number.</summary>
    internal required string TooManyRetryGeneric { get; init; }

    /// <summary>Label of the anchor on the crawler preview page.</summary>
    internal required string PreviewContinue { get; init; }

    /// <summary>Accessible name of a QR image. Contains one <c>{0}</c> placeholder for the URL.</summary>
    internal required string QrAccessibleNameFormat { get; init; }

    /// <summary>Footer label of the support link.</summary>
    internal required string SupportLabel { get; init; }

    /// <summary>Footer label of the privacy link.</summary>
    internal required string PrivacyLabel { get; init; }

    /// <summary>English copy.</summary>
    internal static PageStrings English { get; } = new()
    {
        LanguageTag = "en",
        InterstitialDocumentTitle = "Continue in the app",
        InterstitialHeading = "Continue in the app",
        InterstitialLede = "This browser cannot open the app on its own. Use the button below and the app will open on the right screen.",
        OpenInApp = "Open in the app",
        ContinueToAppStore = "Continue to the App Store",
        ContinueToGooglePlay = "Continue to Google Play",
        ContinueToStore = "Continue to the store",
        ContinueToWebsite = "Continue to the website",
        DestinationLabel = "Destination",
        CountdownRunning = "If nothing happens, this page continues on its own in a moment.",
        CountdownStopped = "Automatic redirect stopped. Use one of the buttons above to continue.",
        StopAutoRedirect = "Stop the automatic redirect",
        ClaimCodeHeading = "Your one-time code",
        ClaimCodeLede = "After installing, enter this code in the app to open the screen this link points to.",
        ClaimCodeNote = "The code is valid for one hour and can be used once.",
        ClaimCodeAccessiblePrefix = "Code",
        NotFoundDocumentTitle = "Link not available",
        NotFoundHeading = "This link is not available",
        NotFoundBody = "The address you opened does not lead anywhere on this domain.",
        NotFoundHint = "If someone sent you this link, ask them to send it again.",
        GoneDocumentTitle = "Link removed",
        GoneHeading = "This link has been removed",
        GoneBody = "It was taken out of service after an abuse report. The destination is no longer served, and this page deliberately does not redirect anywhere.",
        GoneRetention = "The link and the report are kept so that the decision can be reviewed. Nothing was silently deleted.",
        GoneAppealHeading = "Appeal this decision",
        GoneAppealBody = "If you created this link and believe the removal is wrong, you can ask for the decision to be reviewed. Quote the full address you are asking about.",
        GoneAppealFormLabel = "Open the appeal form",
        GoneAppealEmailLabel = "Write to the abuse contact",
        GoneAppealUnconfigured = "Ask the operator of this instance to review the decision.",
        GoneLegalNote = "Notice and action mechanism under Article 16 of the Digital Services Act.",
        TooManyDocumentTitle = "Too many requests",
        TooManyHeading = "Too many requests",
        TooManyBody = "This address received too many requests from your network in a short time and is being rate limited. There is nothing wrong with the link itself.",
        TooManyRetryAfterFormat = "Try again in {0} seconds.",
        TooManyRetryGeneric = "Try again in a moment.",
        PreviewContinue = "Continue",
        QrAccessibleNameFormat = "QR code for {0}",
        SupportLabel = "Support",
        PrivacyLabel = "Privacy",
    };

    /// <summary>Slovak copy.</summary>
    internal static PageStrings Slovak { get; } = new()
    {
        LanguageTag = "sk",
        InterstitialDocumentTitle = "Pokračujte v aplikácii",
        InterstitialHeading = "Pokračujte v aplikácii",
        InterstitialLede = "Tento prehliadač nedokáže otvoriť aplikáciu sám. Použite tlačidlo nižšie a aplikácia sa otvorí na správnej obrazovke.",
        OpenInApp = "Otvoriť v aplikácii",
        ContinueToAppStore = "Pokračovať do App Store",
        ContinueToGooglePlay = "Pokračovať do Google Play",
        ContinueToStore = "Pokračovať do obchodu",
        ContinueToWebsite = "Pokračovať na webovú stránku",
        DestinationLabel = "Cieľ",
        CountdownRunning = "Ak sa nič nestane, stránka bude o chvíľu pokračovať sama.",
        CountdownStopped = "Automatické presmerovanie je zastavené. Pokračujte niektorým tlačidlom vyššie.",
        StopAutoRedirect = "Zastaviť automatické presmerovanie",
        ClaimCodeHeading = "Váš jednorazový kód",
        ClaimCodeLede = "Po inštalácii zadajte tento kód v aplikácii, aby sa otvorila obrazovka, na ktorú odkaz smeruje.",
        ClaimCodeNote = "Kód platí jednu hodinu a dá sa použiť raz.",
        ClaimCodeAccessiblePrefix = "Kód",
        NotFoundDocumentTitle = "Odkaz nie je dostupný",
        NotFoundHeading = "Tento odkaz nie je dostupný",
        NotFoundBody = "Adresa, ktorú ste otvorili, na tejto doméne nikam nevedie.",
        NotFoundHint = "Ak vám odkaz niekto poslal, poproste ho, nech ho pošle znova.",
        GoneDocumentTitle = "Odkaz bol odstránený",
        GoneHeading = "Tento odkaz bol odstránený",
        GoneBody = "Bol vyradený z prevádzky po nahlásení zneužitia. Cieľ sa už neservíruje a táto stránka zámerne nikam nepresmeruje.",
        GoneRetention = "Odkaz aj hlásenie zostávajú uchované, aby sa rozhodnutie dalo preskúmať. Nič nebolo potichu zmazané.",
        GoneAppealHeading = "Odvolanie proti rozhodnutiu",
        GoneAppealBody = "Ak ste tento odkaz vytvorili a myslíte si, že odstránenie je chybné, môžete požiadať o preskúmanie. Uveďte celú adresu, ktorej sa žiadosť týka.",
        GoneAppealFormLabel = "Otvoriť formulár odvolania",
        GoneAppealEmailLabel = "Napísať na kontakt pre zneužitie",
        GoneAppealUnconfigured = "O preskúmanie rozhodnutia požiadajte prevádzkovateľa tejto inštancie.",
        GoneLegalNote = "Mechanizmus nahlasovania a nápravy podľa článku 16 nariadenia o digitálnych službách.",
        TooManyDocumentTitle = "Priveľa požiadaviek",
        TooManyHeading = "Priveľa požiadaviek",
        TooManyBody = "Táto adresa dostala z vašej siete priveľa požiadaviek za krátky čas a je dočasne obmedzená. So samotným odkazom nie je nič v neporiadku.",
        TooManyRetryAfterFormat = "Skúste to znova o {0} sekúnd.",
        TooManyRetryGeneric = "Skúste to znova o chvíľu.",
        PreviewContinue = "Pokračovať",
        QrAccessibleNameFormat = "QR kód pre {0}",
        SupportLabel = "Podpora",
        PrivacyLabel = "Súkromie",
    };

    /// <summary>
    /// Picks the copy for one response: the client's own preference first, the link domain's default
    /// second, the instance default third, English last.
    /// </summary>
    /// <param name="clientLanguage">Primary subtag taken from <c>Accept-Language</c>, lowercase.</param>
    /// <param name="domain">The link domain's runtime configuration, when the host is known.</param>
    /// <param name="options">The instance defaults.</param>
    /// <returns>The copy to render.</returns>
    internal static PageStrings For(string? clientLanguage, DomainRuntimeConfig? domain, InterstitialOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Match(clientLanguage)
            ?? Match(domain?.DefaultLanguage)
            ?? Match(options.DefaultLanguage)
            ?? English;
    }

    /// <summary>Picks the copy for one response from a classified client.</summary>
    /// <param name="client">The classified client.</param>
    /// <param name="domain">The link domain's runtime configuration, when the host is known.</param>
    /// <param name="options">The instance defaults.</param>
    /// <returns>The copy to render.</returns>
    internal static PageStrings For(ClientContext? client, DomainRuntimeConfig? domain, InterstitialOptions options) =>
        For(client?.Language, domain, options);

    private static PageStrings? Match(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        ReadOnlySpan<char> tag = language.AsSpan().Trim();
        int separator = tag.IndexOfAny('-', '_');

        if (separator > 0)
        {
            tag = tag[..separator];
        }

        if (tag.Equals("sk", StringComparison.OrdinalIgnoreCase))
        {
            return Slovak;
        }

        return tag.Equals("en", StringComparison.OrdinalIgnoreCase) ? English : null;
    }
}

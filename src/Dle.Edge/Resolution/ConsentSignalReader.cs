namespace Dle.Edge.Resolution;

/// <summary>
/// Reads the consent signal a plain browser request can carry (§E.6.2, FR-248).
/// </summary>
/// <remarks>
/// <para>
/// Consent is an input to the decision pipeline, not a filter applied to its output (§A, principle
/// 5), so it has to be available before routing runs. On the SDK path it arrives as a structured
/// object with a timestamp; a browser following a link has no such channel, so it can only carry
/// what a consent management platform put on the URL or what the user agent itself declares.
/// </para>
/// <para>
/// Everything here fails closed. A request with no recognised signal returns
/// <see langword="null"/>, and <see cref="ConsentGate"/> treats a missing signal exactly like a
/// refused one: under <see cref="ConsentMode.Full"/> the effective outcome is the same restricted set
/// a tenant on <see cref="ConsentMode.AggregateOnly"/> gets. Nothing in this file can widen what the
/// tenant configured — the gate clamps to the tenant mode — so the worst a forged parameter achieves
/// is consenting on the visitor's behalf to processing the tenant had already enabled, which is why
/// the signal is evidence rather than authorisation and why the IAB scoping parameter is the only one
/// that can turn processing <em>on</em>.
/// </para>
/// <para>
/// The IAB Transparency and Consent string itself is deliberately not parsed. Decoding a TC string
/// correctly means a base64 bit-field walk plus a vendor list, on the hot path, to answer a question
/// whose wrong answer is a regulatory finding; treating "GDPR applies and here is a string" as
/// unproven consent costs a tenant the click-id parameters on that click and costs nobody their
/// rights.
/// </para>
/// </remarks>
public static class ConsentSignalReader
{
    /// <summary>Header a user agent sets to declare a global opt-out.</summary>
    public const string GlobalPrivacyControlHeader = "Sec-GPC";

    /// <summary>Query parameter a consent management platform may put on a link.</summary>
    public const string ConsentQueryKey = "dl_consent";

    /// <summary>IAB parameter declaring whether GDPR applies to this transfer.</summary>
    public const string GdprAppliesQueryKey = "gdpr";

    /// <summary>IAB parameter carrying the Transparency and Consent string.</summary>
    public const string GdprConsentQueryKey = "gdpr_consent";

    /// <summary>Value of <see cref="ConsentQueryKey"/> granting analytics and attribution.</summary>
    public const string ConsentAll = "all";

    /// <summary>Value of <see cref="ConsentQueryKey"/> granting analytics only.</summary>
    public const string ConsentAnalytics = "analytics";

    /// <summary>Value of <see cref="ConsentQueryKey"/> refusing both.</summary>
    public const string ConsentNone = "none";

    /// <summary>Source recorded for a signal taken from a request header.</summary>
    public const string HeaderSource = "header";

    /// <summary>Source recorded for a signal taken from a consent management platform parameter.</summary>
    public const string CmpSource = "cmp";

    /// <summary>Source recorded for a signal derived from the IAB framework parameters.</summary>
    public const string TcfSource = "tcf";

    /// <summary>
    /// Reads the signal, if the request carries one.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <param name="receivedAt">When the request arrived, from <see cref="TimeProvider"/>.</param>
    /// <returns>The signal, or <see langword="null"/> when the request expresses nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public static ConsentSignal? Read(HttpRequest request, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A user agent level opt-out outranks anything on the URL. Sec-GPC is a statement by the
        // person, and a parameter placed there by whoever built the link is not entitled to override
        // it — that is the entire point of the header.
        if (IsAsserted(request.Headers[GlobalPrivacyControlHeader]))
        {
            return Refused(HeaderSource, receivedAt);
        }

        if (request.Query.TryGetValue(ConsentQueryKey, out Microsoft.Extensions.Primitives.StringValues consent) &&
            consent.Count > 0)
        {
            return consent[0] switch
            {
                ConsentAll => Granted(CmpSource, receivedAt),
                ConsentAnalytics => new ConsentSignal
                {
                    Analytics = true,
                    Attribution = false,
                    Timestamp = receivedAt,
                    Source = CmpSource,
                },

                // An unrecognised value is a refusal, not a shrug: it is either a typo in an
                // integration or an attempt to find a value that means "yes" by accident.
                _ => Refused(CmpSource, receivedAt),
            };
        }

        if (request.Query.TryGetValue(GdprAppliesQueryKey, out Microsoft.Extensions.Primitives.StringValues applies) &&
            applies.Count > 0)
        {
            // "gdpr=0" is the framework's way of saying the transfer is out of scope, which is the one
            // case where the absence of a consent string is itself the answer.
            return string.Equals(applies[0], "0", StringComparison.Ordinal)
                ? Granted(TcfSource, receivedAt)
                : Refused(TcfSource, receivedAt);
        }

        return null;
    }

    /// <summary>Whether a header carries the value <c>1</c>.</summary>
    private static bool IsAsserted(Microsoft.Extensions.Primitives.StringValues values) =>
        values.Count > 0 && string.Equals(values[0], "1", StringComparison.Ordinal);

    private static ConsentSignal Granted(string source, DateTimeOffset receivedAt) => new()
    {
        Analytics = true,
        Attribution = true,
        Timestamp = receivedAt,
        Source = source,
    };

    private static ConsentSignal Refused(string source, DateTimeOffset receivedAt) => new()
    {
        Analytics = false,
        Attribution = false,
        Timestamp = receivedAt,
        Source = source,
    };
}

namespace Dle.Edge.Rendering;

/// <summary>
/// The stylesheet the rendered pages carry inline, and the address of the versioned one they link.
/// </summary>
/// <remarks>
/// <para>
/// The split is deliberate. Everything an accessible page cannot do without - the colour tokens for
/// both schemes, the text and background pair that has to meet contrast, the button, the focus ring
/// and the reduced-motion opt-out - is inline, nonce'd, and therefore present even when the stylesheet
/// request never completes, when static file middleware is not mapped, or when the page is rendered
/// into a webview that fetches nothing else. Layout, spacing and decoration live in
/// <see cref="StylesheetPath"/>, which the browser caches across every link on the domain.
/// </para>
/// <para>
/// No <c>@font-face</c>, no <c>@import</c>, no third-party origin, in either half: NFR-14 forbids an
/// outgoing third-party call from anything the resolve path serves, and a webfont is exactly that.
/// The system font stack renders instantly and matches the platform the user is already on.
/// </para>
/// </remarks>
internal static class PageStyles
{
    /// <summary>
    /// Version baked into the stylesheet's filename. Changing a rule in
    /// <c>wwwroot/static/dle.v1.css</c> means renaming the file and bumping this, never editing in
    /// place: the file is served immutable.
    /// </summary>
    internal const string StylesheetVersion = "v1";

    /// <summary>Path of the versioned stylesheet, relative to the host root.</summary>
    internal const string StylesheetPath = "/static/dle." + StylesheetVersion + ".css";

    /// <summary>Path of the icon every page references.</summary>
    internal const string FaviconPath = "/static/favicon.svg";

    /// <summary>
    /// The inline stylesheet. Written with a nonce, so the page needs no <c>unsafe-inline</c>
    /// (T-11, S-04).
    /// </summary>
    /// <remarks>
    /// Contrast, measured against WCAG 2.2 AA: light scheme text #16202e on #f6f7f9 is 13.4:1 and the
    /// muted text is 5.6:1; dark scheme text #e8ecf2 on #0d1117 is 14.1:1 and the muted text is 6.4:1.
    /// The primary button is #ffffff on #1b4dd8 (7.4:1) in the light scheme and #0d1117 on #8ab4ff
    /// (10.2:1) in the dark one. The focus ring is drawn with <c>outline</c> rather than a shadow so
    /// that it survives forced-colours mode, and it never shrinks below the 2px minimum of SC 2.4.13.
    /// </remarks>
    internal const string CriticalCss = """
        *,*::before,*::after{box-sizing:border-box}
        :root{
        color-scheme:light dark;
        --dle-bg:#f6f7f9;--dle-surface:#ffffff;--dle-inset:#f2f4f7;
        --dle-text:#16202e;--dle-muted:#5b6675;
        --dle-border:#dfe3e9;--dle-border-strong:#c3cad4;
        --dle-accent:#1b4dd8;--dle-accent-ink:#153fae;--dle-on-accent:#ffffff;
        --dle-focus:#8a5b00;
        }
        @media (prefers-color-scheme:dark){:root{
        --dle-bg:#0d1117;--dle-surface:#161b22;--dle-inset:#11161d;
        --dle-text:#e8ecf2;--dle-muted:#9aa6b6;
        --dle-border:#2a313b;--dle-border-strong:#3d4652;
        --dle-accent:#8ab4ff;--dle-accent-ink:#a9c8ff;--dle-on-accent:#0d1117;
        --dle-focus:#ffd166;
        }}
        html{-webkit-text-size-adjust:100%}
        body{margin:0;background:var(--dle-bg);color:var(--dle-text);
        font:400 1rem/1.55 system-ui,-apple-system,"Segoe UI",Roboto,"Helvetica Neue",Arial,sans-serif}
        [hidden]{display:none!important}
        a{color:var(--dle-accent-ink)}
        h1,h2,p,ul{margin-top:0}
        .dle-btn{display:flex;align-items:center;justify-content:center;gap:.5rem;
        min-height:3rem;padding:.75rem 1.25rem;border:1px solid transparent;border-radius:.625rem;
        background:var(--dle-accent);color:var(--dle-on-accent);
        font:inherit;font-weight:600;text-align:center;text-decoration:none;cursor:pointer;
        transition:filter .15s ease-in-out}
        .dle-btn:hover{filter:brightness(1.08)}
        :focus-visible{outline:3px solid var(--dle-focus);outline-offset:2px;border-radius:.375rem}
        @media (prefers-reduced-motion:reduce){*{animation:none!important;transition:none!important}}
        @media (forced-colors:active){.dle-btn{border-color:ButtonText}}
        """;
}

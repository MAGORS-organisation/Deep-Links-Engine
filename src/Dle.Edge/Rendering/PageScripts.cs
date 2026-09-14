namespace Dle.Edge.Rendering;

/// <summary>
/// The only script any page the edge renders ever carries: the interstitial's automatic redirect.
/// </summary>
/// <remarks>
/// <para>
/// It is a compiled literal, it is written with the response nonce, and it is the whole of the page's
/// scripting. There is no framework, no bundle and no external origin, because NFR-14 forbids the
/// resolve path from making a third-party call and T-11 is answered by a policy with no
/// <c>unsafe-inline</c> and no <c>unsafe-eval</c> at all.
/// </para>
/// <para>
/// Nothing it does is load bearing. Every value it reads is already in the document — the delay from a
/// <c>data-</c> attribute, the destination from the <c>href</c> the server wrote and validated through
/// <see cref="SafeUrl"/> — and it writes only through <c>textContent</c> and <c>hidden</c>. There is no
/// <c>innerHTML</c>, no <c>eval</c> and no string that becomes markup, so the script adds no injection
/// surface of its own even if an interpolated value were to escape encoding.
/// </para>
/// <para>
/// <b>Why the timer is cancellable by anything at all.</b> WCAG 2.2 success criterion 2.2.1 requires a
/// time limit to be turnable off, and a button alone does not achieve that at a 1.2 second delay: no
/// keyboard or screen reader user can reach it in time. So the first sign of a user doing anything —
/// a key, a pointer press, a focus change, or the page being hidden — cancels the navigation outright,
/// and the visible control is what lets the user confirm that it stayed cancelled. The anchors remain
/// the primary path in every case (§A.2.6, FR-162, NFR-16).
/// </para>
/// </remarks>
internal static class PageScripts
{
    /// <summary>Identifier of the anchor the automatic redirect navigates to.</summary>
    internal const string ContinueId = "dle-continue";

    /// <summary>Identifier of the live region that reports the state of the automatic redirect.</summary>
    internal const string StatusId = "dle-status";

    /// <summary>Identifier of the button that cancels the automatic redirect.</summary>
    internal const string StopId = "dle-stop";

    /// <summary>
    /// Attribute on the continue anchor carrying the delay in milliseconds.
    /// </summary>
    /// <remarks>
    /// It rides on the anchor rather than on <c>body</c> so that the delay is written through
    /// <see cref="HtmlBuilder.Attribute(string, string?)"/> like every other value, instead of being
    /// concatenated into the raw <c>body</c> tag. One rule about where encoding happens is worth more
    /// than the byte it saves.
    /// </remarks>
    internal const string DelayAttribute = "data-dle-delay";

    /// <summary>Attribute on the live region carrying the text shown while the redirect is pending.</summary>
    internal const string RunningTextAttribute = "data-dle-running";

    /// <summary>Attribute on the live region carrying the text shown once the redirect is cancelled.</summary>
    internal const string StoppedTextAttribute = "data-dle-stopped";

    /// <summary>
    /// The automatic redirect. Written only when the page actually has one to perform.
    /// </summary>
    internal const string AutoRedirect =
        "(function(){" +
        "var a=document.getElementById('" + ContinueId + "')," +
        "s=document.getElementById('" + StopId + "'),r=document.getElementById('" + StatusId + "');" +
        "if(!a||!s||!r){return;}" +
        "var d=parseInt(a.getAttribute('" + DelayAttribute + "'),10);" +
        "if(!(d>0)){return;}" +
        "var t=0,done=false;" +
        "function stop(){if(done){return;}done=true;if(t){clearTimeout(t);t=0;}" +
        "s.hidden=true;r.textContent=r.getAttribute('" + StoppedTextAttribute + "')||'';}" +
        "s.addEventListener('click',stop);" +
        "document.addEventListener('keydown',stop,{capture:true,once:true});" +
        "document.addEventListener('pointerdown',stop,{capture:true,once:true});" +
        "document.addEventListener('focusin',stop,{capture:true,once:true});" +
        "window.addEventListener('pagehide',stop);" +
        "document.addEventListener('visibilitychange',function(){" +
        "if(document.visibilityState!=='visible'){stop();}});" +
        "if(document.visibilityState!=='visible'){return;}" +
        "s.hidden=false;" +
        "r.textContent=r.getAttribute('" + RunningTextAttribute + "')||'';" +
        "t=window.setTimeout(function(){if(done){return;}done=true;s.hidden=true;" +
        "window.location.replace(a.href);},d);" +
        "})();";
}

using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dle.UnitTests.Edge;

/// <summary>
/// Executes an <see cref="IResult"/> against a bare <see cref="DefaultHttpContext"/> and captures
/// exactly what a client would receive: the status, the headers and the response bytes. No server,
/// no pipeline — the pages are pure functions of their inputs and are tested as such.
/// </summary>
internal sealed record RenderedPage(int Status, IHeaderDictionary Headers, byte[] Body)
{
    /// <summary>The response body decoded as UTF-8, which is what the pages always write.</summary>
    internal string Html => Encoding.UTF8.GetString(Body);

    /// <summary>The Content-Security-Policy header, or an empty string when there is none.</summary>
    internal string ContentSecurityPolicy => Headers.ContentSecurityPolicy.ToString();

    /// <summary>
    /// The document with every nonce replaced by a placeholder.
    /// </summary>
    /// <remarks>
    /// The nonce is fresh on every response and must be — a reused nonce is the same as no nonce.
    /// It is therefore the one thing that legitimately differs between two responses, and it is
    /// always the same length, so it can be masked before comparing two documents for the
    /// indistinguishability TC-102 and TC-166 are about.
    /// </remarks>
    internal string HtmlWithoutNonces =>
        System.Text.RegularExpressions.Regex.Replace(
            Html,
            "nonce=\"[A-Za-z0-9_-]+\"",
            "nonce=\"·\"",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1));

    internal static async Task<RenderedPage> RenderAsync(IResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };

        using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        return new RenderedPage(context.Response.StatusCode, context.Response.Headers, body.ToArray());
    }
}

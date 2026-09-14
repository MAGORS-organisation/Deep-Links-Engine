using Dle.Edge.Configuration;
using Dle.Edge.Resolution;

using Microsoft.Extensions.Options;

// The data plane (§C.1, §C.3.1). CreateSlimBuilder rather than CreateBuilder: the edge serves one
// route that has to answer inside an 8 ms budget (NFR-01), and the slim host leaves out the default
// middleware, configuration providers and logging sinks that route would otherwise pay for on every
// request without ever using them.
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

// SHARED-KERNEL §15: five module registrations, four endpoint maps, nothing wired by hand. Each
// module owns its own options, its own validation and its own seams, so this file stays a statement
// of what the process is rather than of how any of it works.
builder.Services
    .AddDleEdgeCore(builder.Configuration)
    .AddDleTelemetry(builder.Configuration)
    .AddDleFastPersistence(builder.Configuration)
    .AddDleCrypto(builder.Configuration)
    .AddDleEdgeRateLimiting(builder.Configuration);

WebApplication app = builder.Build();

// Before anything else that reads an address: the rate limit partition key, the click stream's hashed
// identifier and the geographic lookup all derive from it, so a forwarded header has to be resolved
// into HttpContext.Connection.RemoteIpAddress before any of them run. It is only installed when the
// deployment says it sits behind a proxy — trusting the header from an arbitrary peer would let every
// client choose its own rate limit bucket and its own country.
if (app.Services.GetRequiredService<IOptions<NetworkOptions>>().Value.UseForwardedHeaders)
{
    app.UseForwardedHeaders();
}

// Second, so that every response below it — including one written by a component that never returns
// here — carries the hardening headers (S-04, T-11).
app.UseMiddleware<SecurityHeadersMiddleware>();

// The interstitial's versioned stylesheet and icon, plus robots.txt. Served from the same origin
// because the pages' content security policy allows no other, and because NFR-14 forbids the resolve
// path from depending on a third-party origin for anything at all.
//
// Before UseRouting, and that ordering is load bearing: the static file middleware steps aside once an
// endpoint has been selected, and "/{slug}" selects one for every single-segment path — so with the
// two calls the other way round, /robots.txt is answered by the resolver's 404 instead of by the file
// that is sitting in wwwroot.
app.UseStaticFiles();

// Explicitly, so that it lands here rather than at the head of the pipeline where WebApplication would
// otherwise insert it. The rate limiter has to run after it to see the endpoint metadata that lets the
// health probes opt out (§E.9).
app.UseRouting();

app.UseRateLimiter();

app.MapResolve();
app.MapWellKnown();
app.MapQr();
app.MapHealth();

await app.RunAsync();

/// <summary>
/// Entry point of the edge data plane.
/// </summary>
/// <remarks>
/// Declared explicitly so that <c>WebApplicationFactory</c> can name it as the test host's entry
/// point; top-level statements otherwise compile to an internal class the test project cannot see.
/// </remarks>
public partial class Program;

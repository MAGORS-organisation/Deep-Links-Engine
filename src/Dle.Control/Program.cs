using Dle.Control.Configuration;
using Dle.Control.Features.Auth;
using Dle.Control.Features.Bootstrap;
using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Control.Infrastructure;

using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

using Scalar.AspNetCore;

// The control plane's composition root. It calls exactly the eleven module registrations and the
// eleven endpoint maps named in SHARED-KERNEL §15 and nothing else: every module owns its own
// wiring, so this file stays a list of what the deployment unit is made of rather than a place where
// configuration accumulates.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddDleControlCore(builder.Configuration)
    .AddDleTelemetry(builder.Configuration)
    .AddDlePersistence(builder.Configuration)
    .AddDleCrypto(builder.Configuration)
    .AddDleAnalytics(builder.Configuration)
    .AddDleAttribution(builder.Configuration)
    .AddDleAbuse(builder.Configuration)
    .AddDleWebhooks(builder.Configuration)
    .AddDleIdentity(builder.Configuration)
    .AddDleWorkers(builder.Configuration)
    .AddDleBootstrap(builder.Configuration)
    .AddDleControlRateLimiting(builder.Configuration);

WebApplication app = builder.Build();

DleControlOptions control = app.Services.GetRequiredService<IOptions<DleControlOptions>>().Value;

// First, before anything reads an address. Behind Caddy or an ingress every request arrives from the
// proxy, and that address keys the abuse-report and failed-authentication limiters and feeds
// probabilistic matching; unresolved, one client spends everybody's budget. Installed only when the
// deployment names the proxy it sits behind (Dle:Control:Network).
if (app.Services.GetRequiredService<IOptions<ControlNetworkOptions>>().Value.UseForwardedHeaders)
{
    app.UseForwardedHeaders();
}

// An unhandled exception leaves as an RFC 9457 document like every other failure, so an integrator
// never has to parse two error formats. UseStatusCodePages turns a bare status — a 405, a 415 the
// framework produced before any handler ran — into the same shape.
app.UseExceptionHandler();
app.UseStatusCodePages();

// Baseline security headers on all control-plane responses (API, health, docs).
// The admin console applies its full strict CSP in ServeAdminSpaAsync and static file responses.
app.Use(static (context, next) =>
{
    IHeaderDictionary headers = context.Response.Headers;
    headers.TryAdd("X-Content-Type-Options", "nosniff");
    headers.TryAdd("X-Frame-Options", "DENY");
    headers.TryAdd("Referrer-Policy", "no-referrer");
    return next();
});

if (control.ServeAdminSpa)
{
    // Before routing and authorization, and that ordering is load bearing. A request for a file has
    // no endpoint, and a request without an endpoint is judged by the fallback policy, which denies
    // everything: placed after UseAuthorization, the console's own script and stylesheet answered
    // 401 to a browser that had not signed in yet, which is every browser. Only files that exist
    // under wwwroot are served here; everything else falls through to the pipeline below.
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions
    {
        // The console's own files carry its Content-Security-Policy (T-11): the credential it holds
        // in the browser is only as safe as the guarantee that no foreign script runs on its origin.
        OnPrepareResponse = static file =>
        {
            if (AdminConsoleSecurityHeaders.IsConsolePath(file.Context.Request.Path))
            {
                AdminConsoleSecurityHeaders.Apply(file.Context);
            }
        },
    });
}

app.UseRouting();
app.UseCors();

// Buffering has to be switched on before minimal APIs bind the body, which happens before endpoint
// filters run. It applies only to writes that actually carry an Idempotency-Key (§B.7.3).
app.UseMiddleware<IdempotencyBufferingMiddleware>();

// At most one credential per request (T-09): two credentials could otherwise authenticate as one
// tenant and authorise as another. Checked before any authentication scheme runs.
app.UseMiddleware<DleSingleCredentialMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

// The whole of tenant isolation: the caller's tenant is pushed into the ambient tenant context and
// every query filter downstream reads it. No handler compares a tenant identifier, which is what
// makes a foreign identifier indistinguishable from an unknown one (FR-241, TC-166).
//
// It runs AFTER authorization on purpose. An endpoint policy re-authenticates with its own schemes
// and replaces HttpContext.User with the principal it accepted; the scope must come from that
// principal, never from the default scheme's, or the request is scoped to one tenant and authorised
// as another.
app.UseMiddleware<DleTenantScopeMiddleware>();

app.UseRateLimiter();

app.MapLinks();
app.MapDomains();
app.MapApps();
app.MapTenants();
app.MapApiKeys();
app.MapAnalytics();
app.MapAttribution();
app.MapAbuse();
app.MapWebhooks();
app.MapJwks();
app.MapHealth();
app.MapMetrics();
app.MapAuthConfig();

if (app.Environment.IsDevelopment() || control.ExposeApiDocs)
{
    // The document and its reader are enabled in Development, or when explicitly opted into via
    // Dle:Control:ExposeApiDocs (and always subject to read rate limiting).
    app.MapOpenApi()
        .AllowAnonymous()
        .RequireRateLimiting(DleRateLimitPolicies.Read);

    app.MapScalarApiReference(options => options
            .WithTitle("Deep Link Engine — control plane")
            .WithTheme(ScalarTheme.BluePlanet)
            .AddDocument(ControlCoreServiceCollectionExtensions.OpenApiDocumentName))
        .AllowAnonymous()
        .RequireRateLimiting(DleRateLimitPolicies.Read);
}

if (control.ServeAdminSpa)
{
    // The fallback serves the administration application for anything that is not an API path, not a
    // health probe and not a real file. It is deliberately not MapFallbackToFile: that would answer a
    // mistyped API route with the SPA's index page and a 200, and an integrator debugging a typo
    // would spend an afternoon wondering why the API returns HTML.
    app.MapFallback(ServeAdminSpaAsync)
        .AllowAnonymous()
        .DisableRateLimiting()
        .ExcludeFromDescription();
}

await app.RunAsync();

// Serves the administration single-page application, or a problem document for an API path that
// matched no endpoint. A local function rather than a class because it is the composition root's own
// last route and has no other caller.
static async Task ServeAdminSpaAsync(HttpContext context)
{
    PathString path = context.Request.Path;

    bool isApi =
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/readyz", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/livez", StringComparison.OrdinalIgnoreCase);

    if (isApi || !HttpMethods.IsGet(context.Request.Method))
    {
        await Results
            .Problem(
                title: "The resource does not exist.",
                detail: "No route of this API matches the request.",
                statusCode: StatusCodes.Status404NotFound,
                type: ProblemCodes.Base + "not-found")
            .ExecuteAsync(context);

        return;
    }

    IWebHostEnvironment environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
    DleControlOptions options = context.RequestServices
        .GetRequiredService<IOptions<DleControlOptions>>()
        .Value;

    IFileInfo index = environment.WebRootFileProvider.GetFileInfo(options.SpaIndexFile);

    if (!index.Exists)
    {
        // No bundled application in this build. Saying so plainly beats a blank 404 that looks like
        // a routing bug (SHARED-KERNEL §17.9: the branch that cannot serve says why).
        await Results
            .Problem(
                title: "The administration application is not bundled in this build.",
                detail: "Set Dle:Control:ServeAdminSpa to false, or publish the application into "
                    + "wwwroot.",
                statusCode: StatusCodes.Status404NotFound,
                type: ProblemCodes.Base + "not-found")
            .ExecuteAsync(context);

        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    AdminConsoleSecurityHeaders.Apply(context);

    // No-store rather than a long cache: the index page names the hashed asset bundles, so a cached
    // index is how a deployment keeps serving yesterday's application after a release.
    context.Response.Headers.CacheControl = "no-store";

    await context.Response.SendFileAsync(index, context.RequestAborted);
}

/// <summary>
/// Entry point marker, so that the integration tests can reference this assembly's host with
/// <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
/// <remarks>
/// A top-level program's generated entry point class is internal, and the test projects already have
/// <c>InternalsVisibleTo</c>, so declaring the partial class is all that is needed.
/// </remarks>
public partial class Program;

using Dle.Control.Configuration;
using Dle.Control.Features.Shared;
using Dle.Control.Identity;

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
    .AddDleControlRateLimiting(builder.Configuration);

WebApplication app = builder.Build();

DleControlOptions control = app.Services.GetRequiredService<IOptions<DleControlOptions>>().Value;

// An unhandled exception leaves as an RFC 9457 document like every other failure, so an integrator
// never has to parse two error formats. UseStatusCodePages turns a bare status — a 405, a 415 the
// framework produced before any handler ran — into the same shape.
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseRouting();

// Buffering has to be switched on before minimal APIs bind the body, which happens before endpoint
// filters run. It applies only to writes that actually carry an Idempotency-Key (§B.7.3).
app.UseMiddleware<IdempotencyBufferingMiddleware>();

app.UseAuthentication();

// The whole of tenant isolation: the caller's tenant is pushed into the ambient tenant context and
// every query filter downstream reads it. No handler compares a tenant identifier, which is what
// makes a foreign identifier indistinguishable from an unknown one (FR-241, TC-166).
app.UseMiddleware<DleTenantScopeMiddleware>();

app.UseAuthorization();
app.UseRateLimiter();

if (control.ServeAdminSpa)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

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

// The document and its reader are anonymous. A published API surface is not a secret — the same
// document is generated at build time and committed for the contract tests to diff — and requiring a
// credential to read the reference would only mean every integrator starts by guessing.
app.MapOpenApi()
    .AllowAnonymous()
    .DisableRateLimiting();

app.MapScalarApiReference(options => options
        .WithTitle("Deep Link Engine — control plane")
        .WithTheme(ScalarTheme.BluePlanet)
        .AddDocument(ControlCoreServiceCollectionExtensions.OpenApiDocumentName))
    .AllowAnonymous()
    .DisableRateLimiting();

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

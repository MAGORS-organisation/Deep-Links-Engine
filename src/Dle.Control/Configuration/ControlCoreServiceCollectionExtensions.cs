using System.Text.Json;
using System.Text.Json.Serialization;

using Dle.Control.Configuration;
using Dle.Control.Features.Apps;
using Dle.Control.Features.Domains;
using Dle.Control.Features.Health;
using Dle.Control.Features.Links;
using Dle.Control.Features.Shared;
using Dle.Control.Features.Tenants;
using Dle.Control.Infrastructure;
using Dle.Domain.Ports;
using Dle.Persistence.Fast.Caching;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The single composition entry point of the control-plane core (SHARED-KERNEL §15, §C.4).
/// </summary>
/// <remarks>
/// Everything here is either policy the endpoints share — the wire format, the problem document
/// writer, the identifier generator — or a service more than one feature slice needs. A registration
/// used by exactly one slice belongs in that slice, not here.
/// </remarks>
public static class ControlCoreServiceCollectionExtensions
{
    /// <summary>Name of the OpenAPI document the control plane publishes.</summary>
    public const string OpenApiDocumentName = "v1";

    /// <summary>
    /// Registers the control-plane options, the shared services and the OpenAPI document.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// Application configuration; reads <c>Dle:Control</c> and <c>ConnectionStrings:Valkey</c>.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddDleControlCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DleControlOptions>()
            .Bind(configuration.GetSection(DleControlOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                static options => options.MaxPageSize >= options.DefaultPageSize,
                "Dle:Control:MaxPageSize must be at least Dle:Control:DefaultPageSize.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        AddWireFormat(services);
        AddSharedServices(services, configuration);
        AddOutboundHttp(services);
        AddHealthChecks(services);
        AddOpenApi(services);

        return services;
    }

    /// <summary>
    /// Configures the wire format and the problem documents.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <remarks>
    /// <para>
    /// The serializer options mirror <see cref="DleJson.Default"/>: snake_case property names,
    /// snake_case string enumerations, nulls omitted when writing, dictionary keys left alone. The
    /// last of those matters more than it looks — UTM parameters and attribution evidence are data,
    /// and renaming their keys would corrupt them.
    /// </para>
    /// <para>
    /// Reading is strict. Property matching is case sensitive, trailing commas and comments are
    /// refused and the depth is capped, so a typo in a client integration fails loudly instead of
    /// silently dropping a field, and a hostile payload cannot exhaust the stack.
    /// </para>
    /// </remarks>
    private static void AddWireFormat(IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            JsonSerializerOptions json = options.SerializerOptions;

            json.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            json.DictionaryKeyPolicy = null;
            json.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            json.PropertyNameCaseInsensitive = false;
            json.AllowTrailingCommas = false;
            json.ReadCommentHandling = JsonCommentHandling.Disallow;
            json.NumberHandling = JsonNumberHandling.Strict;
            json.WriteIndented = false;
            json.MaxDepth = 32;
            json.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        });

        // A lost PostgreSQL is a 503 with Retry-After, never a 500 (§D.6). Registered ahead of the
        // problem-details fallback, which handles everything this one declines.
        services.AddExceptionHandler<DependencyUnavailableExceptionHandler>();

        services.AddProblemDetails(options => options.CustomizeProblemDetails = static context =>
        {
            // The trace identifier is what turns "it returned 500" into a log query. Nothing else is
            // added: a problem document is returned to a caller who may hold no credential at all,
            // so it carries no request detail, no header value and no address (SHARED-KERNEL §17.5).
            context.ProblemDetails.Extensions["trace_id"] = context.HttpContext.TraceIdentifier;

            context.ProblemDetails.Type ??= ProblemCodes.Base
                + context.HttpContext.Response.StatusCode.ToString(CultureInfo.InvariantCulture);
        });
    }

    /// <summary>Registers the services the feature slices share.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <remarks>
    /// The cache invalidator is registered here rather than left to the edge's module. The control
    /// plane is what changes a link, and a change the edge cannot be told about is a change that
    /// takes effect whenever the cached entry happens to expire — which is exactly what TC-125 rules
    /// out. It is the same implementation the edge registers, over the same two-level cache, so a
    /// removal reaches both levels on every replica.
    /// </remarks>
    private static void AddSharedServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(static provider => new SnowflakeIdGenerator(
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<DleControlOptions>>().Value.NodeId));

        services.TryAddSingleton<IRoutingEngine, RoutingEngine>();
        services.TryAddSingleton<LinkPresentation>();

        services.TryAddScoped<LinkTemplateStore>();
        services.TryAddScoped<LinkWriteService>();
        services.TryAddScoped<AppDomainPairings>();
        services.TryAddScoped<TenantAdministration>();
        services.TryAddScoped<DomainVerificationService>();

        services.AddHybridCache(options => options.ReportTagMetrics = true);

        string? valkey = configuration.GetConnectionString("Valkey");

        if (!string.IsNullOrWhiteSpace(valkey))
        {
            // Valkey speaks the Redis protocol, so the StackExchange.Redis backed IDistributedCache
            // is the shared level; HybridCache picks it up simply by it being registered. Without a
            // connection string the cache is in-process only, which is §B.8 profile A.
            services.AddStackExchangeRedisCache(redis =>
            {
                redis.Configuration = valkey;
                redis.InstanceName = "dle:";
            });
        }

        services.TryAddSingleton<ILinkCacheInvalidator, LinkCacheInvalidator>();
    }

    /// <summary>
    /// Registers the outbound client used to fetch association files.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <remarks>
    /// A verification target is a customer-supplied host name, which makes this an outbound request
    /// to an address the customer chooses — the shape of T-02. The handler validates the address at
    /// the socket rather than the name before it, so nothing can move between the check and the
    /// connection, and it never follows a redirect on its own.
    /// </remarks>
    private static void AddOutboundHttp(IServiceCollection services)
    {
        services.AddHttpClient(DomainVerificationService.HttpClientName, static client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dle-control/1.0 (+https://docs.dle.dev)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        })
            .ConfigurePrimaryHttpMessageHandler(
                static () => PublicEndpointGuard.CreateHandler(TimeSpan.FromSeconds(5)));
    }

    /// <summary>Registers the readiness check.</summary>
    /// <param name="services">The service collection.</param>
    private static void AddHealthChecks(IServiceCollection services) =>
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(
                "postgres",
                tags: [HealthEndpointExtensions.ReadyTag]);

    /// <summary>
    /// Registers the OpenAPI 3.1 document (§B.7.3).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <remarks>
    /// <para>
    /// The XML documentation of every handler and every contract type is surfaced automatically:
    /// <c>GenerateDocumentationFile</c> is on for the whole solution and the OpenAPI package reads
    /// those comments at compile time. That is why the summaries on the endpoints are written as
    /// prose rather than as labels — they are the reference documentation.
    /// </para>
    /// <para>
    /// The document is also emitted to disk during the build, so a contract test can diff it against
    /// the committed copy and a breaking change to the public surface shows up in a pull request
    /// rather than in an integrator's logs.
    /// </para>
    /// </remarks>
    private static void AddOpenApi(IServiceCollection services) =>
        services.AddOpenApi(OpenApiDocumentName, options =>
        {
            options.AddDocumentTransformer(static (document, context, cancellationToken) =>
            {
                document.Info = new()
                {
                    Title = "Deep Link Engine — control plane",
                    Version = "v1",
                    Description =
                        "Link, domain, application, tenant and credential management, plus "
                        + "attribution and analytics reporting. Errors are RFC 9457 problem "
                        + "documents whose type member is one of the stable identifiers documented "
                        + "at https://docs.dle.dev/problems/. Writes accept an Idempotency-Key "
                        + "header: the same key with the same body replays the stored response, and "
                        + "the same key with a different body is a conflict.",
                };

                return Task.CompletedTask;
            });

            options.AddDocumentTransformer<DleSecuritySchemeTransformer>();
            options.AddDocumentTransformer<DleProblemCodeTransformer>();
        });
}

/// <summary>
/// Publishes the stable RFC 9457 problem type identifiers on the document (§B.7.3).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProblemCodes"/> calls these strings part of the public API surface, and an integrator
/// branches on them at least as often as on a status code. Naming only the base URI in the document
/// description left the codes themselves discoverable in two ways only: reading the server's source,
/// or provoking each failure against a running system. This transformer writes the list into the
/// <c>type</c> member of the published <c>ProblemDetails</c> schema, which is where a reader looks
/// for it.
/// </para>
/// <para>
/// They are published as <c>examples</c> and prose rather than as an <c>enum</c> on purpose. The
/// contract permits a new code to be added, and an enumeration would turn every such addition into a
/// breaking change for strict client validators.
/// </para>
/// </remarks>
internal sealed class DleProblemCodeTransformer : Microsoft.AspNetCore.OpenApi.IOpenApiDocumentTransformer
{
    /// <summary>Name the framework gives the problem document schema.</summary>
    private const string SchemaName = "ProblemDetails";

    /// <inheritdoc />
    public Task TransformAsync(
        Microsoft.OpenApi.OpenApiDocument document,
        Microsoft.AspNetCore.OpenApi.OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        string catalogue = string.Join(", ", ProblemCodes.All);

        if (document.Components?.Schemas?.TryGetValue(SchemaName, out Microsoft.OpenApi.IOpenApiSchema? schema) == true
            && schema is Microsoft.OpenApi.OpenApiSchema problem
            && problem.Properties?.TryGetValue("type", out Microsoft.OpenApi.IOpenApiSchema? member) == true
            && member is Microsoft.OpenApi.OpenApiSchema typeMember)
        {
            typeMember.Description =
                "A stable identifier for the kind of failure, and the member to branch on. One of: "
                + catalogue
                + ". A code may be added in a minor version; an existing one never changes meaning.";

            typeMember.Examples = [.. ProblemCodes.All.Select(code => (System.Text.Json.Nodes.JsonNode)code)];
        }

        // Also in the description, so the catalogue survives a change to how the framework names or
        // shapes the problem schema.
        document.Info ??= new Microsoft.OpenApi.OpenApiInfo();
        document.Info.Description += " The problem type identifiers are: " + catalogue + ".";

        return Task.CompletedTask;
    }
}

/// <summary>
/// Declares the credential schemes on the OpenAPI document (FR-242).
/// </summary>
/// <remarks>
/// Two schemes rather than one, because the two credentials really are different: a control-plane
/// key reaches the management API, an SDK key reaches only the two ingestion routes. Publishing them
/// separately is what stops an integrator assuming one token opens everything.
/// </remarks>
internal sealed class DleSecuritySchemeTransformer : Microsoft.AspNetCore.OpenApi.IOpenApiDocumentTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        Microsoft.OpenApi.OpenApiDocument document,
        Microsoft.AspNetCore.OpenApi.OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Components ??= new Microsoft.OpenApi.OpenApiComponents();
        document.Components.SecuritySchemes ??=
            new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>(StringComparer.Ordinal);

        document.Components.SecuritySchemes["ApiKey"] = new Microsoft.OpenApi.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.SecuritySchemeType.Http,
            Scheme = "bearer",
            Description =
                "A control-plane API key, presented as a bearer token or in the X-Api-Key header. "
                + "The key looks like dle_<prefix>_<secret>; only its Argon2id hash is stored, so a "
                + "lost key is replaced rather than recovered.",
        };

        document.Components.SecuritySchemes["SdkKey"] = new Microsoft.OpenApi.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
            Name = "X-Dle-Sdk-Key",
            In = Microsoft.OpenApi.ParameterLocation.Header,
            Description =
                "A key embedded in a customer application. It authenticates the two SDK ingestion "
                + "routes and nothing else, and must be assumed extractable from the application "
                + "binary.",
        };

        return Task.CompletedTask;
    }
}

namespace Dle.Control.Identity;

/// <summary>
/// Pushes the authenticated caller's tenant into <see cref="ITenantContext"/> for the duration of
/// the request (FR-241, T-09, TC-166).
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of tenant isolation in the control plane, and it is deliberately three lines.
/// Everything downstream — every repository, every query, every save — is filtered by
/// <c>DleDbContext</c>'s named tenant filter, which reads exactly this context. No handler compares
/// a tenant identifier, and none may: comparing would mean each new endpoint has one more chance to
/// forget.
/// </para>
/// <para>
/// The consequence is the answer TC-166 asks for. A caller presenting another tenant's link
/// identifier does not hit an ownership check that returns 403; the row simply is not in the
/// queryable, the lookup returns <see langword="null"/>, and the handler answers 404 exactly as it
/// would for an identifier nobody ever issued. The two are indistinguishable from outside because
/// they are indistinguishable inside (SHARED-KERNEL §17.7).
/// </para>
/// <para>
/// The scope is an <c>AsyncLocal</c> that nests and restores, so an <c>await</c> inside the request
/// keeps the tenant and a background continuation started from it does too. A request that
/// authenticates without a usable tenant claim never opens a scope, and the first query it attempts
/// throws <see cref="TenantContextMissingException"/> rather than reading across tenants.
/// </para>
/// </remarks>
public sealed class DleTenantScopeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<DleTenantScopeMiddleware> _logger;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next component in the pipeline.</param>
    /// <param name="tenantContext">The tenant scope every query filter reads.</param>
    /// <param name="logger">Logger.</param>
    public DleTenantScopeMiddleware(
        RequestDelegate next,
        ITenantContext tenantContext,
        ILogger<DleTenantScopeMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <summary>Runs the request inside the caller's tenant scope, when there is one.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        DleCaller? caller = context.User.GetDleCaller();

        if (caller is null)
        {
            // Unauthenticated, or authenticated without a tenant. Either way no scope is opened, so
            // any tenant-owned query further down fails loudly instead of running unscoped.
            await _next(context);
            return;
        }

        using IDisposable scope = _tenantContext.BeginScope(caller.TenantId);

        // The tenant identifier is not an end-user identifier, so it belongs in the log scope; the
        // credential is identified by its non-secret prefix and never by its value (§17.5).
        using IDisposable? logScope = _logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["TenantId"] = caller.TenantId,
            ["ActorType"] = caller.ActorType,
        });

        await _next(context);
    }
}

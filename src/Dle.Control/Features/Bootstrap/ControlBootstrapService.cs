using Dle.Control.Configuration;
using Dle.Control.Identity;
using Dle.Crypto;
using Dle.Domain.Entities;
using Dle.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace Dle.Control.Features.Bootstrap;

/// <summary>
/// Gives an empty installation its first tenant and its first owner key, and tells the identity module
/// which tenant operates the instance.
/// </summary>
/// <remarks>
/// <para>
/// <b>The secret.</b> The owner key is the one credential nobody can obtain any other way, so it has to
/// leave the process once. It leaves by exactly one route: the file named in
/// <see cref="BootstrapOptions.ExportKeyPath"/> when that is set (written owner-only on Unix), or
/// otherwise a block on standard output marked <c>DLE BOOTSTRAP OWNER KEY</c>. It never goes through
/// <see cref="ILogger"/>: the logging pipeline exports to OTLP collectors and log stores that keep it
/// forever and show it to everybody with dashboard access. The log records only the key's non-secret
/// prefix, so the operator can find and revoke it later.
/// </para>
/// <para>
/// <b>The operator.</b> When <c>Dle:Control:InstanceTenantId</c> is not configured, the tenant found
/// under <see cref="BootstrapOptions.DefaultTenantSlug"/> is reported to <see cref="DleInstanceOperator"/>
/// on every start — not only on the start that created it — so the first owner keeps the right to
/// create tenants across restarts and configuration reloads.
/// </para>
/// <para>
/// <b>Several replicas.</b> Two control-plane replicas starting on an empty database both see no
/// tenant. The unique constraint on the tenant lets exactly one insert win; the loser recognises the
/// unique violation, provisions nothing, prints nothing and just discovers the winner's tenant.
/// </para>
/// <para>
/// <b>Failures are loud.</b> A database that cannot be reached is retried a few times and then
/// reported at error level with what to do; it is never swallowed at debug level, which used to leave
/// a fresh installation with no credential and no explanation.
/// </para>
/// </remarks>
public sealed class ControlBootstrapService : IHostedService
{
    /// <summary>The marker an operator greps for in the container output.</summary>
    public const string ConsoleMarker = "DLE BOOTSTRAP OWNER KEY";

    private const string UniqueViolation = "23505";

    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6)];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<BootstrapOptions> _bootstrapOptions;
    private readonly IOptionsMonitor<DleControlOptions> _controlOptions;
    private readonly DleInstanceOperator _instanceOperator;
    private readonly ILogger<ControlBootstrapService> _logger;
    private readonly TextWriter _console;

    /// <summary>Creates the bootstrap service.</summary>
    /// <param name="scopeFactory">Creates the scope the database work runs in.</param>
    /// <param name="bootstrapOptions">Bootstrap configuration.</param>
    /// <param name="controlOptions">Control-plane configuration (the configured operator tenant).</param>
    /// <param name="instanceOperator">Receives the operator tenant the bootstrap provisions or finds.</param>
    /// <param name="logger">Logger. Never receives the secret.</param>
    public ControlBootstrapService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<BootstrapOptions> bootstrapOptions,
        IOptionsMonitor<DleControlOptions> controlOptions,
        DleInstanceOperator instanceOperator,
        ILogger<ControlBootstrapService> logger)
        : this(scopeFactory, bootstrapOptions, controlOptions, instanceOperator, logger, Console.Out)
    {
    }

    /// <summary>Creates the bootstrap service with an explicit standard output, for tests.</summary>
    /// <param name="scopeFactory">Creates the scope the database work runs in.</param>
    /// <param name="bootstrapOptions">Bootstrap configuration.</param>
    /// <param name="controlOptions">Control-plane configuration (the configured operator tenant).</param>
    /// <param name="instanceOperator">Receives the operator tenant the bootstrap provisions or finds.</param>
    /// <param name="logger">Logger. Never receives the secret.</param>
    /// <param name="console">Where the one-time key block is written.</param>
    internal ControlBootstrapService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<BootstrapOptions> bootstrapOptions,
        IOptionsMonitor<DleControlOptions> controlOptions,
        DleInstanceOperator instanceOperator,
        ILogger<ControlBootstrapService> logger,
        TextWriter console)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _bootstrapOptions = bootstrapOptions ?? throw new ArgumentNullException(nameof(bootstrapOptions));
        _controlOptions = controlOptions ?? throw new ArgumentNullException(nameof(controlOptions));
        _instanceOperator = instanceOperator ?? throw new ArgumentNullException(nameof(instanceOperator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        BootstrapOptions options = _bootstrapOptions.CurrentValue;

        if (!options.Enabled)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                string source = _instanceOperator.IsConfigured ? "Dle:Control:InstanceTenantId" : "not set";
                _logger.LogInformation("Bootstrap provisioning is disabled. The operator tenant is {Source}.", source);
            }

            return;
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await RunAsync(options, cancellationToken);
                return;
            }
            catch (Exception exception) when (IsTransient(exception) && attempt < RetryDelays.Length
                                              && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "The database is not reachable yet (attempt {Attempt}); bootstrap retries in {Delay}.",
                    attempt + 1,
                    RetryDelays[attempt]);

                await Task.Delay(RetryDelays[attempt], cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(
                    exception,
                    "Bootstrap could not run: the database is unreachable or not migrated. No tenant and "
                    + "no owner key were provisioned, and the operator tenant is unknown until the next "
                    + "start. Run the migration, make sure PostgreSQL is reachable, then restart dle-control.");
                return;
            }
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Writes the one-time key block in the form the documentation tells operators to grep for.</summary>
    /// <param name="console">Standard output.</param>
    /// <param name="tenantSlug">Slug of the provisioned tenant.</param>
    /// <param name="tenantId">Identifier of the provisioned tenant.</param>
    /// <param name="token">The owner key.</param>
    internal static void WriteKeyBlock(TextWriter console, string tenantSlug, Guid tenantId, string token)
    {
        string rule = new('=', 78);

        console.WriteLine(rule);
        console.WriteLine(ConsoleMarker + " — shown once, store it now");
        console.WriteLine("  tenant: " + tenantSlug + " (" + tenantId.ToString() + ")");
        console.WriteLine("  key:    " + token);
        console.WriteLine("  Sign in to the admin console with this key, create a named owner key for");
        console.WriteLine("  everyday use, then revoke this one (API keys page).");
        console.WriteLine(rule);
        console.Flush();
    }

    /// <summary>Writes the key to a file only its owner can read.</summary>
    /// <param name="path">Target path.</param>
    /// <param name="token">The owner key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the file is written.</returns>
    internal static async Task ExportKeyAsync(string path, string token, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        FileStreamOptions fileOptions = new()
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
        {
            // Owner-only from the moment it exists, and again if it already existed with a wider mode,
            // so the secret is never in a file anyone else can read.
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using (FileStream stream = new(path, fileOptions))
        await using (StreamWriter writer = new(stream))
        {
            await writer.WriteLineAsync(token.AsMemory(), cancellationToken);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private async Task RunAsync(BootstrapOptions options, CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DleDbContext db = scope.ServiceProvider.GetRequiredService<DleDbContext>();

        string slug = string.IsNullOrWhiteSpace(options.DefaultTenantSlug)
            ? "default"
            : options.DefaultTenantSlug.Trim().ToLowerInvariant();

        bool anyTenants;
        using (db.BeginCrossTenantScope("checking whether initial tenant bootstrap is needed"))
        {
            anyTenants = await db.Tenants.AcrossTenants().AnyAsync(cancellationToken);
        }

        if (!anyTenants)
        {
            bool hasPresetKey = !string.IsNullOrWhiteSpace(options.InitialApiKey)
                && ApiKeyHasher.TryReadPrefix(options.InitialApiKey.Trim(), out _);

            if (!hasPresetKey
                && string.IsNullOrWhiteSpace(options.ExportKeyPath)
                && !options.AllowConsoleSecretOutput)
            {
                _logger.LogError(
                    "Bootstrap provisioning skipped: configure Dle:Bootstrap:ExportKeyPath, provide a valid "
                    + "Dle:Bootstrap:InitialApiKey, or set AllowConsoleSecretOutput=true in development.");
            }
            else
            {
                await ProvisionAsync(scope.ServiceProvider, db, options, slug, cancellationToken);
            }
        }

        await DiscoverOperatorAsync(db, slug, cancellationToken);
    }

    private async Task ProvisionAsync(
        IServiceProvider services,
        DleDbContext db,
        BootstrapOptions options,
        string slug,
        CancellationToken cancellationToken)
    {
        ApiKeyHasher hasher = services.GetRequiredService<ApiKeyHasher>();
        ControlPrivacyOptions privacy = services.GetRequiredService<IOptions<ControlPrivacyOptions>>().Value;

        Guid tenantId = _controlOptions.CurrentValue.InstanceTenantId is Guid configured && configured != Guid.Empty
            ? configured
            : Guid.NewGuid();

        Tenant tenant = new()
        {
            Id = tenantId,
            Slug = slug,
            Name = string.IsNullOrWhiteSpace(options.DefaultTenantName) ? "Default Tenant" : options.DefaultTenantName.Trim(),
            Status = "active",
            ConsentMode = privacy.ConsentMode,
        };

        string? presetToken = options.InitialApiKey?.Trim();
        string presetPrefix = string.Empty;
        bool hasPresetToken = !string.IsNullOrEmpty(presetToken)
            && ApiKeyHasher.TryReadPrefix(presetToken, out presetPrefix);

        ApiKeyCredential credential =
            hasPresetToken
                ? hasher.CreateWithToken(presetToken!, presetPrefix)
                : hasher.Create();

        ApiKey apiKey = new()
        {
            TenantId = tenant.Id,
            Name = string.IsNullOrWhiteSpace(options.InitialKeyName) ? "root-bootstrap" : options.InitialKeyName.Trim(),
            Prefix = credential.Prefix,
            Hash = credential.Hash,
            Role = DleRoles.Owner,
            Scopes = [],
        };

        try
        {
            using (db.BeginCrossTenantScope("provisioning initial bootstrap tenant and owner API key"))
            {
                db.Tenants.Add(tenant);
                db.ApiKeys.Add(apiKey);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Another replica provisioned the instance a moment earlier. Nothing to print: its key is
            // the key.
            db.ChangeTracker.Clear();
            _logger.LogInformation("Another control-plane replica provisioned the instance; nothing to do.");
            return;
        }

        string destination = !string.IsNullOrWhiteSpace(options.ExportKeyPath)
            ? "written to " + options.ExportKeyPath
            : options.AllowConsoleSecretOutput && !hasPresetToken
                ? "printed once to standard output under '" + ConsoleMarker + "'"
                : "omitted from stdout (AllowConsoleSecretOutput is false)";

        _logger.LogWarning(
            "Bootstrap provisioned tenant {TenantSlug} ({TenantId}) and owner key {KeyPrefix}. The secret was "
            + "{Destination}; it is not in this log. Revoke that key once a named owner key exists.",
            tenant.Slug,
            tenant.Id,
            credential.Prefix,
            destination);

        if (!string.IsNullOrWhiteSpace(options.ExportKeyPath))
        {
            try
            {
                await ExportKeyAsync(options.ExportKeyPath, credential.Token, cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (!options.AllowConsoleSecretOutput)
                {
                    _logger.LogError(
                        exception,
                        "Could not write the bootstrap key to {ExportPath}, and console secret output is disabled.",
                        options.ExportKeyPath);
                    return;
                }

                // Development fallback: allow console emission only when explicitly opted into.
                _logger.LogError(
                    exception,
                    "Could not write the bootstrap key to {ExportPath}; printing it to standard output instead.",
                    options.ExportKeyPath);
            }
        }

        if (hasPresetToken && !options.AllowConsoleSecretOutput)
        {
            return;
        }

        if (options.AllowConsoleSecretOutput)
        {
            WriteKeyBlock(_console, tenant.Slug, tenant.Id, credential.Token);
        }
    }

    private async Task DiscoverOperatorAsync(DleDbContext db, string slug, CancellationToken cancellationToken)
    {
        if (_instanceOperator.IsConfigured)
        {
            return;
        }

        Guid? found;
        using (db.BeginCrossTenantScope("discovering the operator tenant provisioned by bootstrap"))
        {
            found = await db.Tenants.AcrossTenants()
                .Where(t => t.Slug == slug)
                .Select(t => (Guid?)t.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (found is Guid operatorTenant)
        {
            _instanceOperator.Discovered(operatorTenant);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Operator tenant is {TenantSlug} ({TenantId}); its owners may manage tenants.",
                    slug,
                    operatorTenant);
            }
        }
        else
        {
            _logger.LogWarning(
                "No tenant with slug {TenantSlug} exists and Dle:Control:InstanceTenantId is not set, so no "
                + "tenant may manage other tenants. Set Dle:Control:InstanceTenantId to the operator tenant.",
                slug);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres
        && string.Equals(postgres.SqlState, UniqueViolation, StringComparison.Ordinal);

    private static bool IsTransient(Exception exception) =>
        exception is NpgsqlException { IsTransient: true }
        || exception is TimeoutException
        || exception.InnerException is NpgsqlException { IsTransient: true }
        || exception is InvalidOperationException { InnerException: NpgsqlException };
}

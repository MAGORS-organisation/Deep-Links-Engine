using System.Globalization;
using System.Net.Sockets;
using Dle.Control.Configuration;
using Dle.Control.Identity;
using Dle.Crypto;
using Dle.Domain.Entities;
using Dle.Domain.Privacy;
using Dle.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Dle.Control.Features.Bootstrap;

/// <summary>
/// Hosted service that checks on startup whether the database contains any tenants.
/// If empty and enabled, provisions a root default tenant and initial owner API key (FR-241, FR-242).
/// </summary>
public sealed class ControlBootstrapService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<BootstrapOptions> _bootstrapOptions;
    private readonly IOptionsMonitor<DleControlOptions> _controlOptions;
    private readonly ILogger<ControlBootstrapService> _logger;

    /// <summary>Creates the bootstrap service.</summary>
    public ControlBootstrapService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<BootstrapOptions> bootstrapOptions,
        IOptionsMonitor<DleControlOptions> controlOptions,
        ILogger<ControlBootstrapService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _bootstrapOptions = bootstrapOptions ?? throw new ArgumentNullException(nameof(bootstrapOptions));
        _controlOptions = controlOptions ?? throw new ArgumentNullException(nameof(controlOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        BootstrapOptions options = _bootstrapOptions.CurrentValue;

        if (!options.Enabled)
        {
            _logger.LogDebug("Bootstrap provisioning is disabled by configuration.");
            return;
        }

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            DleDbContext db = scope.ServiceProvider.GetRequiredService<DleDbContext>();
            ApiKeyHasher hasher = scope.ServiceProvider.GetRequiredService<ApiKeyHasher>();
            IOptions<ControlPrivacyOptions> privacyOptions = scope.ServiceProvider.GetRequiredService<IOptions<ControlPrivacyOptions>>();

            bool anyTenants;
            using (db.BeginCrossTenantScope("checking whether initial tenant bootstrap is needed"))
            {
                anyTenants = await db.Tenants.AcrossTenants().AnyAsync(cancellationToken);
            }

            if (anyTenants)
            {
                _logger.LogDebug("Existing tenant(s) present in database. Bootstrap initialization skipped.");
                return;
            }

            _logger.LogInformation("No tenants found in database. Initiating automatic bootstrap provisioning...");

            Guid tenantId = _controlOptions.CurrentValue.InstanceTenantId is Guid instanceId && instanceId != Guid.Empty
                ? instanceId
                : Guid.NewGuid();

            string slug = string.IsNullOrWhiteSpace(options.DefaultTenantSlug)
                ? "default"
                : options.DefaultTenantSlug.Trim().ToLowerInvariant();

            string name = string.IsNullOrWhiteSpace(options.DefaultTenantName)
                ? "Default Tenant"
                : options.DefaultTenantName.Trim();

            string keyName = string.IsNullOrWhiteSpace(options.InitialKeyName)
                ? "root-bootstrap"
                : options.InitialKeyName.Trim();

            string consentMode = privacyOptions.Value.ConsentMode;

            Tenant tenant = new()
            {
                Id = tenantId,
                Slug = slug,
                Name = name,
                Status = "active",
                ConsentMode = consentMode,
            };

            ApiKeyCredential credential;
            string? customToken = options.InitialApiKey?.Trim();
            if (!string.IsNullOrEmpty(customToken) && ApiKeyHasher.TryReadPrefix(customToken, out string customPrefix))
            {
                credential = hasher.CreateWithToken(customToken, customPrefix);
            }
            else
            {
                credential = hasher.Create();
            }

            ApiKey apiKey = new()
            {
                TenantId = tenant.Id,
                Name = keyName,
                Prefix = credential.Prefix,
                Hash = credential.Hash,
                Role = DleRoles.Owner,
                Scopes = [],
            };

            using (db.BeginCrossTenantScope("provisioning initial bootstrap tenant and owner API key"))
            {
                db.Tenants.Add(tenant);
                db.ApiKeys.Add(apiKey);
                await db.SaveChangesAsync(cancellationToken);
            }

            if (_controlOptions.CurrentValue.InstanceTenantId is null || _controlOptions.CurrentValue.InstanceTenantId == Guid.Empty)
            {
                _controlOptions.CurrentValue.InstanceTenantId = tenant.Id;
            }

            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    "\n================================================================================\n" +
                    "[BOOTSTRAP] Initial tenant and root API key provisioned successfully!\n" +
                    "  Tenant ID:    {TenantId}\n" +
                    "  Tenant Slug:  {TenantSlug}\n" +
                    "  API Key Name: {KeyName}\n" +
                    "  API Key:      {Token}\n" +
                    "================================================================================\n" +
                    "IMPORTANT: Save this API Key. It provides Owner access to the Admin Console and API.\n",
                    tenant.Id, tenant.Slug, apiKey.Name, credential.Token);
            }

            if (!string.IsNullOrWhiteSpace(options.ExportKeyPath))
            {
                string exportPath = options.ExportKeyPath;
                try
                {
                    string? exportDir = Path.GetDirectoryName(exportPath);
                    if (!string.IsNullOrEmpty(exportDir) && !Directory.Exists(exportDir))
                    {
                        Directory.CreateDirectory(exportDir);
                    }
                    await File.WriteAllTextAsync(exportPath, credential.Token, cancellationToken);
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Bootstrap API key written to {ExportPath}", exportPath);
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning(ex, "Failed to write bootstrap API key to {ExportPath}", exportPath);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Database is unreachable or uninitialized during bootstrap check. Bootstrap provisioning skipped.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

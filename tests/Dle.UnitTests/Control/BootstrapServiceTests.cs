using Dle.Control.Configuration;
using Dle.Control.Features.Bootstrap;
using Dle.Control.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dle.UnitTests.Control;

/// <summary>
/// Verifies bootstrap configuration and hosted service behavior (FR-241, FR-242).
/// </summary>
public sealed class BootstrapServiceTests
{
    [Fact]
    public void BootstrapOptions_DefaultValues_AreSensible()
    {
        BootstrapOptions options = new();

        Assert.True(options.Enabled);
        Assert.Equal("default", options.DefaultTenantSlug);
        Assert.Equal("Default Tenant", options.DefaultTenantName);
        Assert.Equal("root-bootstrap", options.InitialKeyName);
        Assert.Null(options.InitialApiKey);
        Assert.Null(options.ExportKeyPath);
        Assert.False(options.AllowConsoleSecretOutput);
    }

    [Fact]
    public async Task WhenBootstrapDisabled_StartAsyncReturnsEarlyWithoutError()
    {
        ServiceCollection services = new();
        ServiceProvider provider = services.BuildServiceProvider();

        BootstrapOptions options = new() { Enabled = false };
        TestOptionsMonitor<DleControlOptions> control = new(new DleControlOptions());
        using StringWriter console = new();

        ControlBootstrapService service = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<BootstrapOptions>(options),
            control,
            new DleInstanceOperator(control),
            NullLogger<ControlBootstrapService>.Instance,
            console);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(string.Empty, console.ToString());
    }

    [Fact]
    public void KeyBlock_CarriesTheDocumentedMarkerAndTheKey()
    {
        using StringWriter console = new();
        Guid tenant = Guid.NewGuid();

        ControlBootstrapService.WriteKeyBlock(console, "default", tenant, "dle_abcdefgh_secret");

        string text = console.ToString();
        Assert.Contains("DLE BOOTSTRAP OWNER KEY", text, StringComparison.Ordinal);
        Assert.Contains("dle_abcdefgh_secret", text, StringComparison.Ordinal);
        Assert.Contains(tenant.ToString(), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportKey_WritesTheKeyAndOnUnixOnlyForTheOwner()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dle-bootstrap-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "nested", "owner.key");

        try
        {
            await ControlBootstrapService.ExportKeyAsync(path, "dle_abcdefgh_secret", CancellationToken.None);

            Assert.Equal("dle_abcdefgh_secret", (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Trim());

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InstanceOperator_ConfiguredValueWinsOverDiscovery()
    {
        Guid configured = Guid.NewGuid();
        Guid discovered = Guid.NewGuid();
        DleInstanceOperator instance = new(new TestOptionsMonitor<DleControlOptions>(
            new DleControlOptions { InstanceTenantId = configured }));

        instance.Discovered(discovered);

        Assert.True(instance.IsConfigured);
        Assert.Equal(configured, instance.TenantId);
        Assert.True(instance.IsOperator(configured));
        Assert.False(instance.IsOperator(discovered));
    }

    [Fact]
    public void InstanceOperator_WithoutConfiguration_UsesTheDiscoveredTenant()
    {
        Guid discovered = Guid.NewGuid();
        DleInstanceOperator instance = new(new TestOptionsMonitor<DleControlOptions>(new DleControlOptions()));

        Assert.Null(instance.TenantId);
        Assert.False(instance.IsOperator(discovered));

        instance.Discovered(discovered);

        Assert.False(instance.IsConfigured);
        Assert.True(instance.IsOperator(discovered));
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T currentValue) => CurrentValue = currentValue;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

using Dle.Control.Configuration;
using Dle.Control.Features.Bootstrap;
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
    }

    [Fact]
    public async Task WhenBootstrapDisabled_StartAsyncReturnsEarlyWithoutError()
    {
        ServiceCollection services = new();
        ServiceProvider provider = services.BuildServiceProvider();

        BootstrapOptions options = new() { Enabled = false };
        DleControlOptions controlOptions = new();

        ControlBootstrapService service = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<BootstrapOptions>(options),
            new TestOptionsMonitor<DleControlOptions>(controlOptions),
            NullLogger<ControlBootstrapService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T currentValue) => CurrentValue = currentValue;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

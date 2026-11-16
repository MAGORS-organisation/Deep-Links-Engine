using Dle.Control.Features.Abuse;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dle.UnitTests.Control;

/// <summary>
/// URLhaus enabled without an abuse.ch Auth-Key must refuse to start. Every lookup would fail, a
/// failed lookup counts as no opinion, and every target would pass the reputation check unchecked.
/// </summary>
public sealed class UrlHausOptionsValidationTests
{
    [Fact]
    public void Enabled_WithoutAuthKey_FailsValidation()
    {
        using ServiceProvider provider = Build(enabled: "true", authKey: null);

        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AbuseOptions>>().Value);

        Assert.Contains(AbuseOptions.MissingUrlHausAuthKeyMessage, ex.Failures);
    }

    [Theory]
    [InlineData("false", null)]
    [InlineData("true", "abuse-ch-auth-key")]
    public void Disabled_OrEnabledWithAuthKey_PassesValidation(string enabled, string? authKey)
    {
        using ServiceProvider provider = Build(enabled, authKey);

        AbuseOptions options = provider.GetRequiredService<IOptions<AbuseOptions>>().Value;

        Assert.Equal(bool.Parse(enabled), options.UrlHausEnabled);
    }

    private static ServiceProvider Build(string enabled, string? authKey)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dle:Abuse:UrlHausEnabled"] = enabled,
                ["Dle:Abuse:UrlHausAuthKey"] = authKey,
            })
            .Build();

        ServiceCollection services = new();
        services.AddDleAbuse(configuration);
        return services.BuildServiceProvider();
    }
}

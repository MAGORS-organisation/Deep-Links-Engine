using Dle.Control.Configuration;
using Dle.Control.Features.Auth;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dle.UnitTests.Control;

/// <summary>
/// Verifies the public auth and OIDC configuration endpoint response models (FR-242).
/// </summary>
public sealed class AuthConfigTests
{
    [Fact]
    [Trait("Spec", "FR-242")]
    public void WhenOidcNotConfigured_ReturnsOidcDisabled()
    {
        DleIdentityOptions identity = new();
        identity.Oidc.Authority = null;

        IOptions<DleIdentityOptions> options = Options.Create(identity);
        DleOidcOptions oidc = options.Value.Oidc;

        AuthConfigResponse response = new()
        {
            OidcEnabled = oidc.IsConfigured,
            Authority = oidc.IsConfigured ? oidc.Authority : null,
            ClientId = oidc.IsConfigured ? oidc.ClientId : null,
            Audience = oidc.IsConfigured ? oidc.Audience : null,
            Scopes = oidc.IsConfigured ? [.. oidc.Scopes] : [],
        };

        Assert.False(response.OidcEnabled);
        Assert.Null(response.Authority);
        Assert.Null(response.ClientId);
        Assert.Empty(response.Scopes);
    }

    [Fact]
    [Trait("Spec", "FR-242")]
    public void WhenOidcConfigured_ReturnsConfigDetails()
    {
        DleIdentityOptions identity = new();
        identity.Oidc.Authority = "https://auth.example.com/realms/dle";
        identity.Oidc.ClientId = "dle-admin";
        identity.Oidc.Audience = "dle-control";

        IOptions<DleIdentityOptions> options = Options.Create(identity);
        DleOidcOptions oidc = options.Value.Oidc;

        AuthConfigResponse response = new()
        {
            OidcEnabled = oidc.IsConfigured,
            Authority = oidc.IsConfigured ? oidc.Authority : null,
            ClientId = oidc.IsConfigured ? oidc.ClientId : null,
            Audience = oidc.IsConfigured ? oidc.Audience : null,
            Scopes = oidc.IsConfigured ? [.. oidc.Scopes] : [],
        };

        Assert.True(response.OidcEnabled);
        Assert.Equal("https://auth.example.com/realms/dle", response.Authority);
        Assert.Equal("dle-admin", response.ClientId);
        Assert.Equal("dle-control", response.Audience);
        Assert.Contains("openid", response.Scopes);
    }
}

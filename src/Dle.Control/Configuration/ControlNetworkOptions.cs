using System.ComponentModel.DataAnnotations;

namespace Dle.Control.Configuration;

/// <summary>
/// Reverse proxy handling for the control plane, bound from <c>Dle:Control:Network</c>.
/// </summary>
/// <remarks>
/// The shipped deployments put Caddy or an ingress in front of this process, so without this the
/// address every request arrives from is the proxy's. That address is the partition key of the
/// abuse-report limiter and of the failed-authentication limiter, and the input to probabilistic
/// matching: read from the proxy, one client exhausts everybody's budget and no install can ever be
/// matched by network. The header is honoured only from the peers named here; the same rule, and
/// the same options, as <c>Dle:Edge:Network</c>.
/// </remarks>
public sealed class ControlNetworkOptions
{
    /// <summary>Configuration section this class binds to.</summary>
    public const string SectionName = "Dle:Control:Network";

    /// <summary>Whether <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are honoured.</summary>
    public bool UseForwardedHeaders { get; set; }

    /// <summary>How many proxy hops to walk back through the forwarded chain.</summary>
    [Range(1, 16)]
    public int ForwardLimit { get; set; } = 1;

    /// <summary>Addresses of trusted proxies.</summary>
    public IList<string> KnownProxies { get; } = [];

    /// <summary>Trusted proxy networks in CIDR notation, for example <c>10.0.0.0/8</c>.</summary>
    public IList<string> KnownNetworks { get; } = [];
}

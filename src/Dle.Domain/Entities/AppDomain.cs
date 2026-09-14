namespace Dle.Domain.Entities;

/// <summary>
/// Join row binding an application to a domain. Maps to <c>app_domains</c>.
/// </summary>
/// <remarks>
/// The relationship is many to many because one application is usually reachable through several
/// hosts, and one host frequently serves both the iOS and the Android application. The pairing
/// decides which applications appear in the association files of a host.
/// </remarks>
public class AppDomain
{
    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The domain.</summary>
    public Guid DomainId { get; set; }
}

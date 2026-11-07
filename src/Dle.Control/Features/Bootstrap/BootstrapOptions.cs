namespace Dle.Control.Features.Bootstrap;

/// <summary>
/// Configuration options for automatic bootstrap initialization of the root tenant and API key.
/// Bound from <c>Dle:Bootstrap</c>.
/// </summary>
public sealed class BootstrapOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Dle:Bootstrap";

    /// <summary>
    /// Whether automatic bootstrap initialization is enabled when no tenants exist in the database.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Slug of the initial tenant created when the database has no tenants. Defaults to <c>default</c>.
    /// </summary>
    public string DefaultTenantSlug { get; set; } = "default";

    /// <summary>
    /// Display name of the initial tenant. Defaults to <c>Default Tenant</c>.
    /// </summary>
    public string DefaultTenantName { get; set; } = "Default Tenant";

    /// <summary>
    /// Name assigned to the initial owner API key. Defaults to <c>root-bootstrap</c>.
    /// </summary>
    public string InitialKeyName { get; set; } = "root-bootstrap";

    /// <summary>
    /// Optional predefined API key token (must match <c>dle_&lt;8 chars&gt;_&lt;43 chars&gt;</c>).
    /// If null or empty, a cryptographically random key is minted.
    /// </summary>
    public string? InitialApiKey { get; set; }

    /// <summary>
    /// Optional local file path where the generated bootstrap API key will be written.
    /// Useful for local developer automation and Docker startup scripts.
    /// </summary>
    public string? ExportKeyPath { get; set; }
}

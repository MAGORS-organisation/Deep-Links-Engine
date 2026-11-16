namespace Dle.Control.Features.Bootstrap;

/// <summary>Where the owner key of an empty installation comes from.</summary>
internal enum FirstKeySource
{
    /// <summary>No usable source is configured, so nothing is provisioned.</summary>
    None = 0,

    /// <summary>The operator supplied the key in <see cref="BootstrapOptions.InitialApiKey"/>.</summary>
    Operator = 1,

    /// <summary>
    /// <see cref="BootstrapOptions.InitialApiKey"/> is set but is not an API key; nothing is provisioned.
    /// </summary>
    Malformed = 2,

    /// <summary>
    /// The engine mints the key and hands it out through <see cref="BootstrapOptions.ExportKeyPath"/> or,
    /// when explicitly allowed, standard output.
    /// </summary>
    Generated = 3,
}

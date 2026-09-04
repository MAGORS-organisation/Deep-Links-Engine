namespace Dle.Domain.Clients;

/// <summary>Form factor of the client device. Stored on the click event in lower case.</summary>
public enum DeviceClass
{
    /// <summary>The form factor could not be determined.</summary>
    Unknown = 0,

    /// <summary>A handset.</summary>
    Phone = 1,

    /// <summary>A tablet or a large screen mobile device.</summary>
    Tablet = 2,

    /// <summary>A desktop or laptop computer.</summary>
    Desktop = 3,

    /// <summary>An automated agent: a crawler, a link preview fetcher or a monitoring probe.</summary>
    Bot = 4,
}

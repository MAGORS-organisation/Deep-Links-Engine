namespace Dle.Domain.Clients;

/// <summary>Operating system platform of the client, as classified from the request.</summary>
public enum Platform
{
    /// <summary>The platform could not be determined.</summary>
    Unknown = 0,

    /// <summary>iOS, iPadOS or visionOS.</summary>
    Ios = 1,

    /// <summary>Android, including Android based TV and automotive.</summary>
    Android = 2,

    /// <summary>A desktop operating system: Windows, macOS or Linux.</summary>
    Desktop = 3,

    /// <summary>A platform that is recognised but is none of the above.</summary>
    Other = 4,
}

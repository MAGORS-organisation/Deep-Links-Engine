using System.Reflection;
using System.Reflection.Emit;

using Xunit;

using ControlVersion = Dle.Control.Telemetry.AssemblyVersion;
using EdgeVersion = Dle.Edge.Telemetry.ThisAssembly;

namespace Dle.UnitTests.Telemetry;

/// <summary>
/// The service version the edge and the control plane report: the release tag, not the numeric
/// assembly version that cannot carry a pre-release label.
/// </summary>
public sealed class ProductVersionTests
{
    public static TheoryData<string, string?, string> Cases => new()
    {
        { "edge", "1.0.0-rc.1+0ba672e", "1.0.0-rc.1" },
        { "edge", "1.0.1", "1.0.1" },
        { "edge", null, "2.3.4" },
        { "edge", "", "2.3.4" },
        { "edge", "+0ba672e", "2.3.4" },
        { "control", "1.0.0-rc.1+0ba672e", "1.0.0-rc.1" },
        { "control", "1.0.1", "1.0.1" },
        { "control", null, "2.3.4" },
        { "control", "", "2.3.4" },
        { "control", "+0ba672e", "2.3.4" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Read_PrefersInformationalVersionWithoutBuildMetadata(string plane, string? informational, string expected)
    {
        Assembly assembly = DynamicAssembly(new Version(2, 3, 4, 5), informational);

        string version = plane == "edge" ? EdgeVersion.Read(assembly) : ControlVersion.Read(assembly);

        Assert.Equal(expected, version);
    }

    // A loaded assembly always has a numeric version (0.0.0.0 when none was given), so this covers the
    // unversioned case; the null fallback in Read is defensive only.
    [Theory]
    [InlineData("edge")]
    [InlineData("control")]
    public void Read_WithUnversionedAssembly_ReportsZero(string plane)
    {
        Assembly assembly = DynamicAssembly(version: null, informational: null);

        string version = plane == "edge" ? EdgeVersion.Read(assembly) : ControlVersion.Read(assembly);

        Assert.Equal("0.0.0", version);
    }

    [Fact]
    public void ReportedVersions_AreTheBuiltInformationalVersion()
    {
        Assert.Equal(Expected(typeof(EdgeVersion).Assembly), EdgeVersion.Version);
        Assert.Equal(Expected(typeof(ControlVersion).Assembly), ControlVersion.Value);
        Assert.DoesNotContain('+', EdgeVersion.Version);
        Assert.DoesNotContain('+', ControlVersion.Value);

        static string Expected(Assembly assembly) =>
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    }

    private static AssemblyBuilder DynamicAssembly(Version? version, string? informational)
    {
        var name = new AssemblyName($"Dle.VersionProbe.{Guid.NewGuid():N}") { Version = version };
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
        if (informational is not null)
        {
            ConstructorInfo constructor = typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!;
            assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [informational]));
        }

        return assembly;
    }
}

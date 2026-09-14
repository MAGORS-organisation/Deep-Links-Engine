using System.Runtime.CompilerServices;

namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// A fact that needs the container-backed infrastructure of <see cref="DleInfrastructureFixture"/>.
/// </summary>
/// <remarks>
/// <para>
/// On a machine with a container runtime this behaves exactly like <see cref="FactAttribute"/>. On
/// a machine without one the test is <em>skipped</em>, with <see cref="DockerRequirement.SkipReason"/>
/// as the reason — never failed and never silently passed. The condition is evaluated before the
/// test class is constructed, so a skipped test touches no fixture and starts no container.
/// </para>
/// <para>
/// Setting <c>DLE_TESTS_REQUIRE_DOCKER=1</c> removes the skip, so the same test binary that reports
/// a readable skip on a laptop reports a hard failure on a build server that was supposed to have
/// Docker and does not.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler. Do not pass a value.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler. Do not pass a value.</param>
    public RequiresDockerFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = DockerRequirement.SkipReason;
        SkipType = typeof(DockerRequirement);
        SkipUnless = nameof(DockerRequirement.ShouldRun);
    }
}

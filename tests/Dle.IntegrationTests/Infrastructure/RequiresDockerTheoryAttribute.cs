using System.Runtime.CompilerServices;

namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// A theory that needs the container-backed infrastructure of <see cref="DleInfrastructureFixture"/>.
/// </summary>
/// <remarks>
/// The <see cref="RequiresDockerFactAttribute"/> rules apply unchanged: without a container runtime
/// every row is skipped with <see cref="DockerRequirement.SkipReason"/>, and
/// <c>DLE_TESTS_REQUIRE_DOCKER=1</c> turns that skip into a failure.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresDockerTheoryAttribute : TheoryAttribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler. Do not pass a value.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler. Do not pass a value.</param>
    public RequiresDockerTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = DockerRequirement.SkipReason;
        SkipType = typeof(DockerRequirement);
        SkipUnless = nameof(DockerRequirement.ShouldRun);
    }
}

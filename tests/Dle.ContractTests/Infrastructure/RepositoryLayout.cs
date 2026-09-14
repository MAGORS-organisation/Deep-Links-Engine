namespace Dle.ContractTests.Infrastructure;

/// <summary>
/// Locates the checked-out repository from the test binary.
/// </summary>
/// <remarks>
/// The committed contract files are read from the source tree rather than from the build output.
/// A copied file would let a regeneration land in <c>bin</c> and never reach the commit, which is the
/// one failure mode a contract baseline must not have: the gate would pass locally, pass in CI, and
/// still let the breaking change through because the thing being compared was itself the new
/// document.
/// </remarks>
public static class RepositoryLayout
{
    /// <summary>Root of the checkout, found by walking up to the solution file.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>Directory holding the committed contract baselines and schemas.</summary>
    public static string ContractsDirectory { get; } =
        Path.Combine(Root, "tests", "Dle.ContractTests", "Contracts");

    /// <summary>Reads a committed contract file.</summary>
    /// <param name="fileName">File name inside <see cref="ContractsDirectory"/>.</param>
    /// <returns>The file's text with line endings normalized to <c>\n</c>.</returns>
    public static string ReadContract(string fileName) =>
        File.ReadAllText(Path.Combine(ContractsDirectory, fileName))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Overwrites a committed contract file.</summary>
    /// <param name="fileName">File name inside <see cref="ContractsDirectory"/>.</param>
    /// <param name="content">The new content, already normalized.</param>
    public static void WriteContract(string fileName, string content)
    {
        Directory.CreateDirectory(ContractsDirectory);
        File.WriteAllText(Path.Combine(ContractsDirectory, fileName), content);
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Dle.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "The repository root was not found above " + AppContext.BaseDirectory +
            ". The contract tests read their committed baselines from the source tree.");
    }
}

using System.Text.RegularExpressions;
using Dle.ContractTests.Infrastructure;

namespace Dle.ContractTests.Traceability;

/// <summary>
/// Every <c>[Trait("Spec", …)]</c> in the suites points at something the specification defines.
/// </summary>
/// <remarks>
/// <para>
/// The traits are how coverage is read by requirement: a report grouped by them answers "which of
/// §A.4 is tested". That answer is only worth having if the identifiers are real. An invented or
/// mistyped <c>FR-3xx</c> does not fail anything by itself — it quietly credits a requirement that
/// does not exist and leaves the one the test really covers looking untested, which is the failure
/// mode this guards against.
/// </para>
/// <para>
/// The scan reads the test sources rather than reflecting over the assemblies, so one project can
/// check the traits of all of them without referencing them.
/// </para>
/// </remarks>
public sealed partial class SpecTraitTests
{
    [Fact]
    public void EverySpecTraitOnATest_NamesARequirementTheSpecificationDefines()
    {
        string specPath = Path.Combine(RepositoryLayout.Root, "docs", "zadanie.md");
        string specIdsPath = Path.Combine(RepositoryLayout.Root, "tests", "Dle.ContractTests", "Traceability", "spec-ids.txt");
        string? specification = File.Exists(specPath) ? File.ReadAllText(specPath) : null;
        HashSet<string> knownIds = new(
            File.ReadLines(specIdsPath)
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0 && !line.StartsWith('#')),
            StringComparer.Ordinal);
        string testsRoot = Path.Combine(RepositoryLayout.Root, "tests");

        SortedSet<string> missing = new(StringComparer.Ordinal);
        SortedSet<string> checkedIdentifiers = new(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in SpecTrait().Matches(File.ReadAllText(file)))
            {
                string identifier = match.Groups["id"].Value;

                // Only the numbered requirements are checked. A section or ADR reference is a
                // pointer into prose, and §A.4 is the only list with identifiers to check against.
                if (!identifier.StartsWith("FR-", StringComparison.Ordinal)
                    && !identifier.StartsWith("NFR-", StringComparison.Ordinal))
                {
                    continue;
                }

                _ = checkedIdentifiers.Add(identifier);

                bool defined = specification is not null
                    ? specification.Contains($"| {identifier} |", StringComparison.Ordinal) && knownIds.Contains(identifier)
                    : knownIds.Contains(identifier);

                if (!defined)
                {
                    _ = missing.Add($"{identifier} (first seen in {Path.GetFileName(file)})");
                }
            }
        }

        Assert.True(checkedIdentifiers.Count > 0, "no Spec traits were found at all, so this test is checking nothing");
        Assert.True(
            missing.Count == 0,
            "These Spec traits name requirements missing from the specification baseline (docs/zadanie.md or spec-ids.txt), "
            + "so a report grouped by requirement credits the wrong one:\n  " + string.Join("\n  ", missing));
    }

    [GeneratedRegex(@"Trait\(""Spec"",\s*""(?<id>[^""]+)""\)")]
    private static partial Regex SpecTrait();
}

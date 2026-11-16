using System.Text;

using Dle.Analytics.Postgres;
using Dle.Domain.Analytics;

using Xunit;

namespace Dle.UnitTests.Analytics;

/// <summary>
/// Regression tests for CSV export formula neutralization.
/// </summary>
public sealed class CsvAnalyticsExporterTests
{
    [Theory]
    [InlineData("=cmd|' /C calc'!A0", "'=cmd|' /C calc'!A0")]
    [InlineData("+SUM(A1:A2)", "'+SUM(A1:A2)")]
    [InlineData("-1+2", "'-1+2")]
    [InlineData("@HYPERLINK(\"https://evil.example\")", "'@HYPERLINK(\"\"https://evil.example\"\")")]
    [InlineData("\t=cmd", "'\t=cmd")]
    [InlineData("=already\"quoted", "'=already\"\"quoted")]
    public async Task WriteBreakdownAsync_PrefixesFormulaTriggerCharacters(string key, string expectedCellContent)
    {
        var exporter = new CsvAnalyticsExporter();
        using var output = new MemoryStream();

        await exporter.WriteBreakdownAsync(
            output,
            "utm_source",
            [new BreakdownRow(key, Clicks: 5, Installs: 1, Value: null)],
            TestContext.Current.CancellationToken);

        string csv = Encoding.UTF8.GetString(output.ToArray());
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Contains(expectedCellContent, lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteBreakdownAsync_LeavesOrdinaryValuesUnprefixed()
    {
        var exporter = new CsvAnalyticsExporter();
        using var output = new MemoryStream();

        await exporter.WriteBreakdownAsync(
            output,
            "country",
            [new BreakdownRow("SK", Clicks: 12, Installs: 3, Value: 42.5m)],
            TestContext.Current.CancellationToken);

        string csv = Encoding.UTF8.GetString(output.ToArray());
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("country,SK,12,3,42.5", lines[1]);
    }
}

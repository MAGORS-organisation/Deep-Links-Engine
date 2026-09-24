using ClickHouse.Client.ADO;
using ClickHouse.Client.ADO.Parameters;

namespace Dle.Analytics.ClickHouse;

/// <summary>
/// Accumulates the server-side parameters of one ClickHouse statement.
/// </summary>
/// <remarks>
/// Every value a report filters by travels through this type. Nothing is ever interpolated into
/// the statement text except fragments chosen from a fixed allowlist (bucket functions, dimension
/// columns), which is what keeps CA2100 honest here rather than merely quiet.
/// </remarks>
internal sealed class ClickHouseParameterSet
{
    private readonly Dictionary<string, (string Type, object Value)> _parameters =
        new(StringComparer.Ordinal);

    /// <summary>Adds or replaces a parameter rendered as <c>{name:Type}</c> in the statement.</summary>
    /// <param name="name">Parameter name without braces.</param>
    /// <param name="clickHouseType">ClickHouse type name used both in the placeholder and by the
    /// driver to format the value.</param>
    /// <param name="value">The value. Never <see langword="null"/>: an absent filter is omitted
    /// from the statement instead of being passed as a null parameter.</param>
    public void Add(string name, string clickHouseType, object value)
    {
        _parameters[name] = (clickHouseType, value);
    }

    /// <summary>Copies the accumulated parameters onto a command.</summary>
    /// <param name="command">The command about to be executed.</param>
    public void ApplyTo(ClickHouseCommand command)
    {
        foreach ((string name, (string type, object value)) in _parameters)
        {
            ClickHouseDbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.ClickHouseType = type;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}

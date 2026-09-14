using System.Data.Common;
using System.Net;

namespace Dle.Persistence.Fast.Data;

/// <summary>
/// Conversions between Postgres column values and the shared-kernel types, done by hand.
/// </summary>
/// <remarks>
/// <para>
/// Every reader here takes an ordinal, never a column name: the queries in this assembly are literal
/// and their projections are fixed, so a name lookup would only buy a dictionary probe per column on
/// the path with the tightest latency budget in the product (NFR-01).
/// </para>
/// <para>
/// The enum decoding is deliberately forgiving about spelling. Enumerations are stored as text rather
/// than integers (SHARED-KERNEL §12) and the exact casing depends on how the control plane's EF Core
/// model configures the conversion, which is a separate slice. Accepting <c>aggregate_only</c>,
/// <c>AggregateOnly</c> and <c>1</c> alike costs nothing here and removes a whole class of
/// cross-module breakage; an unrecognised value falls back to the most restrictive mode, never the
/// most permissive one.
/// </para>
/// </remarks>
internal static class PostgresValues
{
    /// <summary>Reads a nullable string column.</summary>
    internal static string? String(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>Reads a nullable <see cref="DateTimeOffset"/> from a <c>timestamptz</c> column.</summary>
    internal static DateTimeOffset? Timestamp(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    /// <summary>Reads a nullable <see cref="Guid"/> column.</summary>
    internal static Guid? Uuid(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    /// <summary>Reads a nullable <c>text[]</c> column as an array, mapping SQL null to an empty array.</summary>
    internal static string[] StringArray(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? [] : reader.GetFieldValue<string[]>(ordinal);

    /// <summary>
    /// Decodes a consent mode stored as text.
    /// </summary>
    /// <param name="raw">The stored value, or <see langword="null"/>.</param>
    /// <param name="fallback">The value to use when <paramref name="raw"/> is null or unrecognised.</param>
    /// <returns>The decoded mode.</returns>
    /// <remarks>
    /// An unrecognised value resolves to <paramref name="fallback"/>, and every caller passes
    /// <see cref="ConsentMode.Off"/> for it. A typo in the database must never widen what may be
    /// collected about a person (§E.6.2).
    /// </remarks>
    internal static ConsentMode ParseConsentMode(string? raw, ConsentMode fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        ReadOnlySpan<char> value = raw.AsSpan().Trim();

        if (Matches(value, "off") || value is "0")
        {
            return Dle.Domain.Privacy.ConsentMode.Off;
        }

        if (Matches(value, "aggregateonly") || value is "1")
        {
            return Dle.Domain.Privacy.ConsentMode.AggregateOnly;
        }

        if (Matches(value, "full") || value is "2")
        {
            return Dle.Domain.Privacy.ConsentMode.Full;
        }

        return fallback;
    }

    /// <summary>
    /// Parses a stored network prefix such as <c>203.0.113.0/24</c> into the value Npgsql writes to an
    /// <c>inet</c> column.
    /// </summary>
    /// <param name="value">The prefix produced by <c>IIpHasher.Prefix</c>, or <see langword="null"/>.</param>
    /// <param name="inet">The parsed value.</param>
    /// <returns><see langword="false"/> when the value is absent or not a valid address.</returns>
    internal static bool TryParseInet(string? value, out NpgsqlInet inet)
    {
        inet = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        ReadOnlySpan<char> span = value.AsSpan().Trim();
        int slash = span.IndexOf('/');

        if (slash < 0)
        {
            if (!IPAddress.TryParse(span, out IPAddress? bare))
            {
                return false;
            }

            inet = new NpgsqlInet(bare);
            return true;
        }

        if (!IPAddress.TryParse(span[..slash], out IPAddress? address) ||
            !byte.TryParse(span[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte netmask))
        {
            return false;
        }

        int maxBits = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;

        if (netmask > maxBits)
        {
            return false;
        }

        inet = new NpgsqlInet(address, netmask);
        return true;
    }

    /// <summary>
    /// Trims a value to a fixed-width <c>character(n)</c> column, returning <see langword="null"/> when
    /// it does not fit.
    /// </summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="length">The exact width the column requires.</param>
    /// <returns>The upper-cased value, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A binary <c>COPY</c> into <c>char(2)</c> aborts the whole batch when one row carries three
    /// characters, so a malformed country code has to be turned into a null here. Losing one row's
    /// country is a rounding error; losing five thousand rows is an outage of the click stream.
    /// </remarks>
    internal static string? FixedWidth(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();

        return trimmed.Length == length ? trimmed.ToUpperInvariant() : null;
    }

    private static bool Matches(ReadOnlySpan<char> value, string expected)
    {
        int index = 0;

        foreach (char c in value)
        {
            if (c is '_' or '-' or ' ')
            {
                continue;
            }

            if (index >= expected.Length || char.ToLowerInvariant(c) != expected[index])
            {
                return false;
            }

            index++;
        }

        return index == expected.Length;
    }
}

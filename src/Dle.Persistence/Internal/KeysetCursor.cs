using System.Buffers.Text;
using System.Text;

namespace Dle.Persistence.Internal;

/// <summary>
/// Encodes and decodes the opaque cursor of a keyset paged list.
/// </summary>
/// <remarks>
/// The cursor is the sort key of the last row of the page — an instant and an identifier — encoded
/// base64url so that it survives a URL untouched. It is opaque by convention, not by cryptography:
/// it carries nothing a caller could not read from the row it already received, so signing it would
/// protect nothing. Decoding is total and never throws; a mangled cursor simply starts from the
/// beginning.
/// </remarks>
internal static class KeysetCursor
{
    /// <summary>Builds a cursor from the sort key of the last row on a page.</summary>
    /// <param name="createdAt">Creation instant of that row.</param>
    /// <param name="id">Identifier of that row.</param>
    /// <returns>The encoded cursor.</returns>
    internal static string Encode(DateTimeOffset createdAt, long id)
    {
        string raw = string.Create(
            CultureInfo.InvariantCulture,
            $"{createdAt.UtcTicks}:{id}");

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>Reads a cursor produced by <see cref="Encode"/>.</summary>
    /// <param name="cursor">The cursor, possibly null or mangled.</param>
    /// <param name="createdAt">Creation instant of the last row seen.</param>
    /// <param name="id">Identifier of the last row seen.</param>
    /// <returns><see langword="true"/> when the cursor was readable.</returns>
    internal static bool TryDecode(string? cursor, out DateTimeOffset createdAt, out long id)
    {
        createdAt = default;
        id = 0;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Base64Url.DecodeFromChars(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        string raw = Encoding.UTF8.GetString(decoded);
        int separator = raw.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == raw.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(
                raw.AsSpan(0, separator),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long ticks)
            || !long.TryParse(
                raw.AsSpan(separator + 1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long parsedId))
        {
            return false;
        }

        if (ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        id = parsedId;
        return true;
    }
}

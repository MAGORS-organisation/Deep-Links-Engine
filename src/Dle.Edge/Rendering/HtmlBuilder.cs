using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

namespace Dle.Edge.Rendering;

/// <summary>
/// A very small HTML writer: raw markup goes in through <see cref="Raw(string)"/>, everything else is
/// encoded on the way in.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that "did someone forget to encode this?" is answerable by reading the call sites
/// rather than the templates. Only <see cref="Raw(string)"/> writes unencoded text, only the renderers
/// call it, and it is only ever called with literals the compiler can see. Open Graph metadata, link
/// titles, branding and anything reaching the page from the query string travel through
/// <see cref="Text(string?)"/> or <see cref="Attribute(string, string?)"/> (T-11).
/// </para>
/// <para>
/// The encoder is <see cref="HtmlEncoder.Default"/>, which escapes the five markup characters and
/// everything outside the basic Latin range. That is stricter than necessary for a UTF-8 document and
/// costs a few bytes on Slovak copy, and it is worth it: an encoder that emits raw non-ASCII has to be
/// trusted to normalise correctly against every parser quirk, and this one does not have to be.
/// </para>
/// </remarks>
internal sealed class HtmlBuilder
{
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;
    private readonly StringBuilder _buffer;

    /// <summary>Creates a writer.</summary>
    /// <param name="capacity">Initial buffer size. The rendered pages sit between 3 and 6 kB.</param>
    internal HtmlBuilder(int capacity = 6 * 1024) => _buffer = new StringBuilder(capacity);

    /// <summary>Appends markup verbatim. Only ever called with compile-time literals.</summary>
    /// <param name="markup">The markup.</param>
    /// <returns>This instance.</returns>
    internal HtmlBuilder Raw(string markup)
    {
        _buffer.Append(markup);
        return this;
    }

    /// <summary>Appends HTML-encoded text. A <see langword="null"/> or empty value writes nothing.</summary>
    /// <param name="text">The text.</param>
    /// <returns>This instance.</returns>
    internal HtmlBuilder Text(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _buffer.Append(Encoder.Encode(text));
        }

        return this;
    }

    /// <summary>
    /// Appends <c> name="value"</c> with the value encoded, or nothing when the value is absent.
    /// </summary>
    /// <param name="name">Attribute name; a literal.</param>
    /// <param name="value">Attribute value; untrusted.</param>
    /// <returns>This instance.</returns>
    internal HtmlBuilder Attribute(string name, string? value)
    {
        if (value is null)
        {
            return this;
        }

        _buffer.Append(' ').Append(name).Append("=\"").Append(Encoder.Encode(value)).Append('"');

        return this;
    }

    /// <summary>Appends an integer in the invariant culture.</summary>
    /// <param name="value">The value.</param>
    /// <returns>This instance.</returns>
    internal HtmlBuilder Number(int value)
    {
        _buffer.Append(value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    /// <summary>
    /// Appends a <c>&lt;meta&gt;</c> element, or nothing when the content is absent.
    /// </summary>
    /// <param name="attributeName">Either <c>property</c> or <c>name</c>, depending on the vocabulary.</param>
    /// <param name="key">Metadata key, for example <c>og:title</c>.</param>
    /// <param name="content">Metadata value; untrusted.</param>
    /// <returns>This instance.</returns>
    internal HtmlBuilder Meta(string attributeName, string key, string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return this;
        }

        return Raw("<meta ")
            .Raw(attributeName)
            .Raw("=\"")
            .Raw(key)
            .Raw("\"")
            .Attribute("content", content)
            .Raw(">\n");
    }

    /// <inheritdoc />
    public override string ToString() => _buffer.ToString();
}

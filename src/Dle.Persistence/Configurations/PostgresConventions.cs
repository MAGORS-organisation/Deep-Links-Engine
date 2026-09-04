namespace Dle.Persistence.Configurations;

/// <summary>
/// The PostgreSQL type and default names the entity configurations share.
/// </summary>
/// <remarks>
/// They are constants rather than literals scattered across twenty files so that the DDL of §B.5.2
/// and the model can be compared line by line, and so that changing one of them is a single edit.
/// </remarks>
internal static class PostgresConventions
{
    /// <summary>Case insensitive text, used for slugs and hosts (requires the <c>citext</c> extension).</summary>
    internal const string CiText = "citext";

    /// <summary>Binary JSON. Every JSON column in the schema is <c>jsonb</c>, never <c>json</c>.</summary>
    internal const string Jsonb = "jsonb";

    /// <summary>An array of text, mapped from <see cref="List{T}"/> of <see cref="string"/>.</summary>
    internal const string TextArray = "text[]";

    /// <summary>Fixed precision confidence, <c>numeric(3,2)</c> per §B.5.3.</summary>
    internal const string Numeric32 = "numeric(3,2)";

    /// <summary>
    /// Server-side identifier default. Resolves to native <c>uuidv7()</c> on PostgreSQL 18 and to a
    /// RFC 9562 compliant fallback on PostgreSQL 16 and 17, so the minimum supported version keeps
    /// time ordered keys (ADR-003).
    /// </summary>
    internal const string UuidV7Default = "dle_uuidv7()";

    /// <summary>Server-side timestamp default, matching <c>DEFAULT now()</c> in the DDL.</summary>
    internal const string NowDefault = "now()";

    /// <summary>Default for a <c>jsonb</c> object column.</summary>
    internal const string EmptyJsonObjectDefault = "'{}'::jsonb";

    /// <summary>Default for a <c>jsonb</c> array column.</summary>
    internal const string EmptyJsonArrayDefault = "'[]'::jsonb";

    /// <summary>Default for a <c>text[]</c> column.</summary>
    internal const string EmptyTextArrayDefault = "'{}'::text[]";
}

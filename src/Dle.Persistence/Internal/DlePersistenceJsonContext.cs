using System.Text.Json.Serialization;

namespace Dle.Persistence.Internal;

/// <summary>
/// Source generated metadata for the documents this project writes itself.
/// </summary>
/// <remarks>
/// Only one shape needs it: the link snapshot stored with every revision (FR-107). It is generated
/// rather than reflected so the assembly stays trimmable and so the layout of a stored revision is
/// fixed at compile time — a revision has to deserialize years later, and a snapshot whose shape
/// depends on runtime reflection is a snapshot whose shape can drift.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Link))]
internal sealed partial class DlePersistenceJsonContext : JsonSerializerContext;

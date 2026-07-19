using System.Text;
using System.Text.Json.Serialization;

namespace AIFlashcardMaker.ChartsStudio.Domain.Specs;

/// <summary>
/// Charts Studio Phase 2 — the complete, serializable, versioned description of ONE figure.
///
/// THE SINGLE SOURCE OF TRUTH. The contact-sheet thumbnail, the on-screen figure and (later)
/// the 600-DPI export all derive from this one object through the same renderer. That is what
/// guarantees WYSIWYG: there is no second code path that could disagree.
///
/// Deliberately holds NO DATA. A spec says what to draw and how; the numbers are resolved from
/// the AnalysisContext at render time. Keeping data out means a spec stays small, stays valid
/// across data changes, and can be compared for staleness rather than silently carrying a stale
/// copy of the figures it was built from.
///
/// The user-edit overlay (FigurePatch) arrives with the Figure Editor in a later phase. The
/// spec is already shaped for it: regeneration replaces the base, the patch re-applies.
/// </summary>
public sealed class FigureSpec
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Stable identity for this figure, persisted and used as a cache key component.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Chart form, by ChartTypeRegistry id. Renaming an id needs a migration.</summary>
    [JsonPropertyName("chartTypeId")]
    public string ChartTypeId { get; set; } = "";

    /// <summary>
    /// The variable this figure is about, by ContextVariable id. Phase 2 forms are all
    /// single-variable; the field is a list so multi-variable forms are an additive change.
    /// </summary>
    [JsonPropertyName("variableIds")]
    public List<string> VariableIds { get; set; } = new();

    /// <summary>Figure title. Empty means "derive from the variable and form".</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>Value-axis label. Empty means derive.</summary>
    [JsonPropertyName("valueAxisLabel")]
    public string ValueAxisLabel { get; set; } = "";

    /// <summary>Category-axis label. Empty means derive.</summary>
    [JsonPropertyName("categoryAxisLabel")]
    public string CategoryAxisLabel { get; set; } = "";

    /// <summary>
    /// The computed result this figure is bound to, when one exists.
    ///
    /// Not used for rendering in Phase 2 — binding drives provenance and staleness, which
    /// arrive with the Figure Editor. Present now so a figure saved today already records what
    /// it was built alongside, rather than needing a retroactive backfill.
    /// </summary>
    [JsonPropertyName("boundResultId")]
    public string? BoundResultId { get; set; }

    /// <summary>
    /// The context fingerprint this figure was generated from. A mismatch on reopen is what
    /// marks a saved figure stale.
    /// </summary>
    [JsonPropertyName("sourceFingerprint")]
    public string SourceFingerprint { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Primary variable, or empty. Convenience for the single-variable Phase 2 forms.</summary>
    [JsonIgnore]
    public string PrimaryVariableId => VariableIds.Count > 0 ? VariableIds[0] : "";

    /// <summary>
    /// Everything that can change what this figure LOOKS like, in a fixed order.
    ///
    /// Used as the render cache key together with size and DPI. Deliberately excludes Id and
    /// CreatedAt: two figures of the same variable in the same form are the same picture and
    /// should share a cached render rather than each paying to draw it.
    /// </summary>
    public string ToRenderKey()
    {
        var sb = new StringBuilder();
        sb.Append(SchemaVersion).Append('|')
          .Append(ChartTypeId).Append('|')
          .Append(string.Join(",", VariableIds)).Append('|')
          .Append(Title).Append('|')
          .Append(ValueAxisLabel).Append('|')
          .Append(CategoryAxisLabel).Append('|')
          .Append(SourceFingerprint);
        return sb.ToString();
    }

    public FigureSpec Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Id = Id,
        ChartTypeId = ChartTypeId,
        VariableIds = new List<string>(VariableIds),
        Title = Title,
        ValueAxisLabel = ValueAxisLabel,
        CategoryAxisLabel = CategoryAxisLabel,
        BoundResultId = BoundResultId,
        SourceFingerprint = SourceFingerprint,
        CreatedAt = CreatedAt
    };
}

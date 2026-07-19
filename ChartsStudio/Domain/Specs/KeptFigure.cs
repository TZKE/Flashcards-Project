using System.Text.Json.Serialization;

namespace AIFlashcardMaker.ChartsStudio.Domain.Specs;

/// <summary>
/// Charts Studio Phase 3/4 — one figure the user has KEPT: the immutable recommendation, the
/// user's patch, and shelf metadata. This is the unit the Figure Shelf displays and the unit
/// persistence stores (schema v2 — see ChartsStudioProjectState).
///
/// The spec is stored ONCE, here. The patch never duplicates anything the spec already says —
/// it holds only deviations — so there is exactly one copy of the recommendation data and one
/// overlay of user intent, and the two can never drift apart.
/// </summary>
public sealed class KeptFigure
{
    /// <summary>The recommendation, immutable once kept. Renders derive from Spec + Patch;
    /// nothing in the editor may write into this object.</summary>
    [JsonPropertyName("spec")]
    public FigureSpec Spec { get; set; } = new();

    /// <summary>The user's edits, or null when the figure is untouched. Always stored in
    /// canonical form (null rather than empty — see FigurePatch.Canonicalize).</summary>
    [JsonPropertyName("patch")]
    public FigurePatch? Patch { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set every time the patch changes. Null = never edited.</summary>
    [JsonPropertyName("lastEditedAt")]
    public DateTime? LastEditedAt { get; set; }

    /// <summary>Reserved for a future favourites feature — persisted now so turning it on is
    /// additive, never surfaced as a control in this phase.</summary>
    [JsonPropertyName("isFavorite")]
    public bool IsFavorite { get; set; }

    /// <summary>Reserved for future per-figure notes. Same reasoning as IsFavorite.</summary>
    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    /// <summary>Identity is the spec's identity — a kept figure IS its recommendation.</summary>
    [JsonIgnore]
    public string Id => Spec.Id;

    /// <summary>True when the user has changed anything — drives the shelf's Modified badge.</summary>
    [JsonIgnore]
    public bool IsModified => Patch is not null && !Patch.IsEmpty;

    /// <summary>Deep copy with a NEW identity, for Duplicate. The copy is a fully independent
    /// figure: editing the duplicate's patch can never bleed into the original.</summary>
    public KeptFigure DuplicateWithNewId()
    {
        var spec = Spec.Clone();
        spec.Id = Guid.NewGuid().ToString("N");

        return new KeptFigure
        {
            Spec = spec,
            Patch = Patch?.Clone(),
            CreatedAt = DateTime.UtcNow,
            LastEditedAt = null,
            IsFavorite = IsFavorite,
            Notes = Notes
        };
    }
}

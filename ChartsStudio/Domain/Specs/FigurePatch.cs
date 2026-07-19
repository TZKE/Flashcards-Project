using System.Text;
using System.Text.Json.Serialization;

namespace AIFlashcardMaker.ChartsStudio.Domain.Specs;

/// <summary>
/// Charts Studio Phase 3 — the USER'S EDITS to one figure, as an overlay.
///
/// THE CENTRAL RULE OF THE EDITOR
///
///     Recommended figure  +  user patch  =  rendered figure
///
/// The recommendation (FigureSpec) is immutable once kept. Everything the user changes lives
/// here, as a nullable field: null means "no opinion — use the recommendation/theme default",
/// non-null means "the user chose this". That single convention gives, for free:
///
///   • RESET is deletion. Reset a field = null it. Reset the figure = drop the patch.
///   • REGENERATION is safe. When a spec is rebuilt from new data, the patch re-applies on
///     top; the user's title survives a data refresh because it was never stored in the spec.
///   • DIRTY is comparison. Two patches are equal iff their keys are equal — no field-by-field
///     bookkeeping.
///   • UNDO is snapshots. A patch is small and cheap to clone, so history is a stack of
///     patches rather than a command hierarchy that must be kept reversible by hand.
///
/// The patch NEVER carries data or statistics — only presentation. A patch applied to a spec
/// whose numbers changed shows the new numbers with the old styling, which is exactly right.
///
/// EXTENSIBILITY: adding an override is adding a nullable property here plus a line in ToKey,
/// Clone and the resolver. Nothing else in the module changes — history, persistence, dirty
/// tracking and caching all operate on the patch as an opaque value.
/// </summary>
public sealed class FigurePatch
{
    // ---- Text -----------------------------------------------------------------------

    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }

    /// <summary>Figure caption. Shown with the figure in the editor and (later) exports;
    /// deliberately NOT drawn into the chart image — captions belong under figures.</summary>
    [JsonPropertyName("caption")] public string? Caption { get; set; }

    [JsonPropertyName("xAxisTitle")] public string? XAxisTitle { get; set; }
    [JsonPropertyName("yAxisTitle")] public string? YAxisTitle { get; set; }

    // ---- Visibility -----------------------------------------------------------------

    [JsonPropertyName("showXAxis")] public bool? ShowXAxis { get; set; }
    [JsonPropertyName("showYAxis")] public bool? ShowYAxis { get; set; }
    [JsonPropertyName("showGrid")] public bool? ShowGrid { get; set; }
    [JsonPropertyName("showLegend")] public bool? ShowLegend { get; set; }

    /// <summary>Reserved: custom annotations do not exist yet, but their visibility toggle is
    /// part of the persisted shape now so adding them later is additive.</summary>
    [JsonPropertyName("showAnnotations")] public bool? ShowAnnotations { get; set; }

    // ---- Appearance -----------------------------------------------------------------

    /// <summary>Theme preset id (see FigureThemes). Individual overrides below beat the theme.</summary>
    [JsonPropertyName("themeId")] public string? ThemeId { get; set; }

    /// <summary>Palette id used when colouring bar categories individually.</summary>
    [JsonPropertyName("paletteId")] public string? PaletteId { get; set; }

    /// <summary>When true, a multi-category bar chart colours each category from the palette.
    /// One flag covers bar/box/mean because a figure has exactly one chart form.</summary>
    [JsonPropertyName("colorByCategory")] public bool? ColorByCategory { get; set; }

    /// <summary>Series colour (#RRGGBB) — fills the box, the bars, or the mean marker.</summary>
    [JsonPropertyName("seriesColorHex")] public string? SeriesColorHex { get; set; }

    [JsonPropertyName("backgroundColorHex")] public string? BackgroundColorHex { get; set; }

    [JsonPropertyName("fontFamily")] public string? FontFamily { get; set; }
    [JsonPropertyName("titleFontSize")] public double? TitleFontSize { get; set; }
    [JsonPropertyName("axisFontSize")] public double? AxisFontSize { get; set; }
    [JsonPropertyName("tickFontSize")] public double? TickFontSize { get; set; }

    [JsonPropertyName("lineWidth")] public double? LineWidth { get; set; }
    [JsonPropertyName("markerSize")] public double? MarkerSize { get; set; }

    /// <summary>Fill opacity, 0.05–1.</summary>
    [JsonPropertyName("opacity")] public double? Opacity { get; set; }

    /// <summary>"vertical" | "horizontal". Only honoured where the form supports it (bar).</summary>
    [JsonPropertyName("orientation")] public string? Orientation { get; set; }

    /// <summary>Escape hatch for future per-figure extras (e.g. custom annotations) without a
    /// schema break. Unknown keys survive load/save untouched.</summary>
    [JsonPropertyName("extras")] public Dictionary<string, string>? Extras { get; set; }

    // ---- Value semantics ------------------------------------------------------------

    [JsonIgnore]
    public bool IsEmpty =>
        Title is null && Subtitle is null && Caption is null &&
        XAxisTitle is null && YAxisTitle is null &&
        ShowXAxis is null && ShowYAxis is null && ShowGrid is null && ShowLegend is null &&
        ShowAnnotations is null &&
        ThemeId is null && PaletteId is null && ColorByCategory is null &&
        SeriesColorHex is null && BackgroundColorHex is null &&
        FontFamily is null && TitleFontSize is null && AxisFontSize is null && TickFontSize is null &&
        LineWidth is null && MarkerSize is null && Opacity is null &&
        Orientation is null &&
        (Extras is null || Extras.Count == 0);

    public FigurePatch Clone() => new()
    {
        Title = Title, Subtitle = Subtitle, Caption = Caption,
        XAxisTitle = XAxisTitle, YAxisTitle = YAxisTitle,
        ShowXAxis = ShowXAxis, ShowYAxis = ShowYAxis, ShowGrid = ShowGrid, ShowLegend = ShowLegend,
        ShowAnnotations = ShowAnnotations,
        ThemeId = ThemeId, PaletteId = PaletteId, ColorByCategory = ColorByCategory,
        SeriesColorHex = SeriesColorHex, BackgroundColorHex = BackgroundColorHex,
        FontFamily = FontFamily, TitleFontSize = TitleFontSize,
        AxisFontSize = AxisFontSize, TickFontSize = TickFontSize,
        LineWidth = LineWidth, MarkerSize = MarkerSize, Opacity = Opacity,
        Orientation = Orientation,
        Extras = Extras is null ? null : new Dictionary<string, string>(Extras)
    };

    /// <summary>
    /// Deterministic identity of the SET fields. Drives dirty detection, undo de-duplication
    /// and the render cache key — two patches with the same key are the same edit. Field order
    /// is fixed and culture-invariant so the key is stable across sessions and machines.
    /// </summary>
    public string ToKey()
    {
        var sb = new StringBuilder();
        void Add(string name, object? v)
        {
            if (v is null) return;
            sb.Append(name).Append('=');
            sb.Append(v switch
            {
                double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                bool b => b ? "1" : "0",
                _ => v.ToString()
            });
            sb.Append(';');
        }

        Add("title", Title); Add("subtitle", Subtitle); Add("caption", Caption);
        Add("xAxis", XAxisTitle); Add("yAxis", YAxisTitle);
        Add("showX", ShowXAxis); Add("showY", ShowYAxis);
        Add("grid", ShowGrid); Add("legend", ShowLegend); Add("annot", ShowAnnotations);
        Add("theme", ThemeId); Add("palette", PaletteId); Add("byCat", ColorByCategory);
        Add("series", SeriesColorHex); Add("bg", BackgroundColorHex);
        Add("font", FontFamily); Add("titleSz", TitleFontSize);
        Add("axisSz", AxisFontSize); Add("tickSz", TickFontSize);
        Add("line", LineWidth); Add("marker", MarkerSize); Add("opacity", Opacity);
        Add("orient", Orientation);

        if (Extras is { Count: > 0 })
            foreach (var kv in Extras.OrderBy(k => k.Key, StringComparer.Ordinal))
                Add("x:" + kv.Key, kv.Value);

        return sb.ToString();
    }

    /// <summary>Key of a possibly-null patch — "" for null and for empty, so "no patch" and
    /// "patch with nothing in it" compare equal everywhere.</summary>
    public static string KeyOf(FigurePatch? patch) =>
        patch is null || patch.IsEmpty ? "" : patch.ToKey();

    /// <summary>Null when the patch says nothing — the canonical stored form, so persistence
    /// and dirty checks never see an empty-but-allocated patch.</summary>
    public static FigurePatch? Canonicalize(FigurePatch? patch) =>
        patch is null || patch.IsEmpty ? null : patch;
}

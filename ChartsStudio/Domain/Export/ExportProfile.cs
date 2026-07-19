namespace AIFlashcardMaker.ChartsStudio.Domain.Export;

/// <summary>
/// Charts Studio Phase 5 — the canonical design surface, and the WYSIWYG contract in numbers.
///
/// Every figure is laid out on ONE logical canvas: 672 × 426 logical units. The editor
/// preview, the export preview and every export render this same logical canvas — only the
/// ScaleFactor differs, and ScottPlot scales fonts, line weights and tick layout with it
/// proportionally. Same logical canvas + proportional scale = same title position, same tick
/// labels, same spacing, at 96 DPI on screen and at 600 DPI in print. This was verified
/// empirically in the Phase 0 renderer spike and is re-verified by pixel-grid comparison in QA.
///
/// The corollary the numbers enforce: **a profile chooses physical size and DPI, never
/// layout.** Anything that would change layout (a different aspect ratio, a font multiplier)
/// is only reachable through the Custom profile, where changing the figure's shape is exactly
/// what the user asked for — and the preview shows the real result before a file is written.
/// </summary>
public static class ExportCanvas
{
    public const int LogicalWidth = 672;
    public const int LogicalHeight = 426;

    public static double AspectRatio => (double)LogicalWidth / LogicalHeight;

    /// <summary>Height in inches that preserves the canonical aspect for a given width.</summary>
    public static double HeightForWidth(double widthInches) => widthInches / AspectRatio;
}

/// <summary>
/// Charts Studio Phase 5 — one immutable export profile.
///
/// A profile is a POLICY: physical size, DPI and background handling. It is deliberately not
/// editable in place — "the Journal profile" must mean the same thing in every export and
/// every manifest, forever. Custom exports are a NEW profile value built from user options,
/// never a mutation of a built-in.
/// </summary>
public sealed record ExportProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }

    public required double WidthInches { get; init; }
    public required double HeightInches { get; init; }
    public required int Dpi { get; init; }

    /// <summary>Breathing room between the canvas edge and the figure, in inches.</summary>
    public double PaddingInches { get; init; }

    /// <summary>PNG/SVG only; PDF always flattens to an opaque page (documented).</summary>
    public bool TransparentBackground { get; init; }

    /// <summary>
    /// Multiplies the resolved style's font sizes. 1.0 for every built-in profile — a font
    /// multiplier CHANGES LAYOUT and therefore breaks the "what you approved" guarantee, so
    /// only Custom may carry a different value, and the preview shows the consequence.
    /// </summary>
    public double FontScale { get; init; } = 1.0;

    /// <summary>Colour handling. RGB is the only mode; monochrome output is a THEME decision
    /// (the Mono theme) so it shows in the editor, never a hidden export-time substitution.</summary>
    public string ColorMode { get; init; } = "RGB";

    /// <summary>Anti-aliasing. Always on — Skia's high-quality path; recorded for the manifest.</summary>
    public bool AntiAlias { get; init; } = true;

    // ---- Derived geometry (the one place this math lives) ----------------------------

    public int PixelWidth => (int)Math.Round(WidthInches * Dpi);
    public int PixelHeight => (int)Math.Round(HeightInches * Dpi);
    public int PaddingPixels => (int)Math.Round(PaddingInches * Dpi);

    /// <summary>Pixels available to the figure itself, inside the padding.</summary>
    public int InnerPixelWidth => Math.Max(16, PixelWidth - 2 * PaddingPixels);
    public int InnerPixelHeight => Math.Max(16, PixelHeight - 2 * PaddingPixels);

    /// <summary>
    /// The logical height this profile lays out against. Built-ins keep the canonical aspect,
    /// so this equals ExportCanvas.LogicalHeight; a Custom profile with a different aspect
    /// gets a proportionally different logical height — the layout adapts to the shape the
    /// user chose, deterministically, and the preview shows it.
    /// </summary>
    public int LogicalHeightForAspect =>
        (int)Math.Round(ExportCanvas.LogicalWidth * ((double)InnerPixelHeight / InnerPixelWidth));

    /// <summary>The WYSIWYG lever: how much the logical canvas is magnified for this output.</summary>
    public double ScaleFactor => (double)InnerPixelWidth / ExportCanvas.LogicalWidth;

    public double WidthPoints => WidthInches * 72.0;
    public double HeightPoints => HeightInches * 72.0;
}

/// <summary>
/// The built-in profiles. Sizes follow publishing conventions: 3.5 in is the standard
/// single-column figure width, 7 in double-column; 300 DPI is the common raster floor for
/// publication, 600 DPI the safe line-art target.
/// </summary>
public static class ExportProfiles
{
    public static readonly ExportProfile Journal = new()
    {
        Id = "journal",
        DisplayName = "Journal (single column)",
        Description = "3.5 in wide at 600 DPI — the standard single-column figure, submission-ready.",
        WidthInches = 3.5,
        HeightInches = ExportCanvas.HeightForWidth(3.5),
        Dpi = 600
    };

    public static readonly ExportProfile JournalDouble = new()
    {
        Id = "journal-double",
        DisplayName = "Journal (double column)",
        Description = "7.0 in wide at 600 DPI for full-width figures.",
        WidthInches = 7.0,
        HeightInches = ExportCanvas.HeightForWidth(7.0),
        Dpi = 600
    };

    public static readonly ExportProfile Publication = new()
    {
        Id = "publication",
        DisplayName = "Publication",
        Description = "5.0 in wide at 300 DPI — theses, reports and grant applications.",
        WidthInches = 5.0,
        HeightInches = ExportCanvas.HeightForWidth(5.0),
        Dpi = 300
    };

    public static readonly ExportProfile Presentation = new()
    {
        Id = "presentation",
        DisplayName = "Presentation",
        Description = "Slide-sized (1920 px wide) for talks and lectures.",
        WidthInches = 10.0,
        HeightInches = ExportCanvas.HeightForWidth(10.0),
        Dpi = 192
    };

    public static readonly ExportProfile Poster = new()
    {
        Id = "poster",
        DisplayName = "Poster",
        Description = "12 in wide at 300 DPI for conference posters.",
        WidthInches = 12.0,
        HeightInches = ExportCanvas.HeightForWidth(12.0),
        Dpi = 300,
        PaddingInches = 0.15
    };

    public static readonly ExportProfile Thumbnail = new()
    {
        Id = "thumbnail",
        DisplayName = "Thumbnail",
        Description = "Small screen-resolution preview image.",
        WidthInches = 3.5,
        HeightInches = ExportCanvas.HeightForWidth(3.5),
        Dpi = 96
    };

    public static IReadOnlyList<ExportProfile> BuiltIn { get; } =
        new[] { Journal, JournalDouble, Publication, Presentation, Poster, Thumbnail };

    /// <summary>
    /// Builds a Custom profile from user overrides, clamped to sane bounds. This is the ONLY
    /// path to a profile that differs from the built-ins — built-ins are never mutated.
    /// </summary>
    public static ExportProfile Custom(
        double widthInches, double heightInches, int dpi,
        double paddingInches = 0, bool transparent = false, double fontScale = 1.0)
    {
        widthInches = Math.Clamp(widthInches, 1.0, 40.0);
        heightInches = Math.Clamp(heightInches, 1.0, 40.0);
        dpi = Math.Clamp(dpi, 72, 1200);
        paddingInches = Math.Clamp(paddingInches, 0, Math.Min(widthInches, heightInches) / 4.0);
        fontScale = Math.Clamp(fontScale, 0.5, 2.0);

        return new ExportProfile
        {
            Id = "custom",
            DisplayName = "Custom",
            Description = "User-defined size and resolution.",
            WidthInches = widthInches,
            HeightInches = heightInches,
            Dpi = dpi,
            PaddingInches = paddingInches,
            TransparentBackground = transparent,
            FontScale = fontScale
        };
    }
}

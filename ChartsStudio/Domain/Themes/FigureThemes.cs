namespace AIFlashcardMaker.ChartsStudio.Domain.Themes;

/// <summary>One theme preset: the coherent visual baseline a patch deviates from.</summary>
public sealed class FigureThemeDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    public required string BackgroundHex { get; init; }
    public required string AxisHex { get; init; }
    public required string GridHex { get; init; }
    public required string SeriesFillHex { get; init; }
    public required string FontFamily { get; init; }

    public required double TitleFontSize { get; init; }
    public required double AxisFontSize { get; init; }
    public required double TickFontSize { get; init; }
    public required double LineWidth { get; init; }
    public required double MarkerSize { get; init; }
    public required double Opacity { get; init; }

    public bool ShowGrid { get; init; } = true;
}

/// <summary>One categorical palette, used when colouring bar categories individually.</summary>
public sealed class FigurePalette
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string[] Hexes { get; init; }
}

/// <summary>
/// Charts Studio Phase 3 — the theme and palette catalogue.
///
/// CODE, NOT USER DATA. Themes are descriptors like chart types: shipping them inside user
/// files would mean migrating them forever. A patch stores only a theme ID; what that ID means
/// can improve between versions and every figure quietly benefits.
///
/// A THEME CAN NEVER CHANGE VALIDITY. It touches colours, fonts and weights only — which is
/// why switching themes is always safe, while switching chart forms is gated.
///
/// Three presets, deliberately: enough to cover print, mono-print and slides; few enough that
/// each can be kept genuinely good. A theme editor is explicitly out of scope.
/// </summary>
public static class FigureThemes
{
    public const string DefaultThemeId = "journal";
    public const string DefaultPaletteId = "default";

    private static readonly FigureThemeDefinition[] Themes =
    {
        new()
        {
            Id = "journal",
            DisplayName = "Journal",
            BackgroundHex = "#FFFFFF",
            AxisHex = "#333333",
            GridHex = "#ECECEC",
            SeriesFillHex = "#2F6FB2",
            FontFamily = "Segoe UI",
            TitleFontSize = 14, AxisFontSize = 12, TickFontSize = 10,
            LineWidth = 1.5, MarkerSize = 12, Opacity = 0.75
        },
        new()
        {
            // Greyscale-safe: many journals still print mono, and what prints safely is also
            // what survives a poor projector. Accessibility and print rules converge here.
            Id = "mono",
            DisplayName = "Mono (print-safe)",
            BackgroundHex = "#FFFFFF",
            AxisHex = "#000000",
            GridHex = "#DDDDDD",
            SeriesFillHex = "#8C8C8C",
            FontFamily = "Times New Roman",
            TitleFontSize = 14, AxisFontSize = 12, TickFontSize = 10,
            LineWidth = 1.5, MarkerSize = 12, Opacity = 0.9
        },
        new()
        {
            Id = "presentation",
            DisplayName = "Presentation",
            BackgroundHex = "#FFFFFF",
            AxisHex = "#222222",
            GridHex = "#E8E8F5",
            SeriesFillHex = "#1E88E5",
            FontFamily = "Segoe UI",
            TitleFontSize = 18, AxisFontSize = 14, TickFontSize = 12,
            LineWidth = 2.5, MarkerSize = 16, Opacity = 0.85
        }
    };

    private static readonly FigurePalette[] Palettes =
    {
        new()
        {
            Id = "default",
            DisplayName = "OrbitLab",
            Hexes = new[] { "#2F6FB2", "#E07B39", "#4CAF50", "#9C27B0", "#F44336", "#00838F", "#795548", "#607D8B" }
        },
        new()
        {
            // Okabe–Ito: the standard colourblind-safe set. Ships as an option now, becomes
            // the argued-for default in the accessibility pass.
            Id = "colorblind-safe",
            DisplayName = "Colourblind-safe",
            Hexes = new[] { "#0072B2", "#E69F00", "#009E73", "#CC79A7", "#D55E00", "#56B4E9", "#F0E442", "#000000" }
        },
        new()
        {
            Id = "grayscale",
            DisplayName = "Grayscale",
            Hexes = new[] { "#333333", "#6E6E6E", "#9E9E9E", "#C4C4C4", "#4F4F4F", "#858585", "#B3B3B3", "#1A1A1A" }
        }
    };

    public static IReadOnlyList<FigureThemeDefinition> All => Themes;
    public static IReadOnlyList<FigurePalette> AllPalettes => Palettes;

    /// <summary>Unknown ids fall back to the default rather than failing — a figure saved with
    /// a theme a future version removed must still open.</summary>
    public static FigureThemeDefinition Get(string? id) =>
        Themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal))
        ?? Themes[0];

    public static FigurePalette GetPalette(string? id) =>
        Palettes.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))
        ?? Palettes[0];
}

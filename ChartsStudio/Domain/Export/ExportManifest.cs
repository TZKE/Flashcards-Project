using System.Text.Json.Serialization;

namespace AIFlashcardMaker.ChartsStudio.Domain.Export;

/// <summary>
/// Charts Studio Phase 5 — the manifest written with every package export.
///
/// This is the reproducibility record: enough to answer, months later, "exactly what produced
/// these files?" — which app version, which renderer, which profile geometry, and for every
/// figure its spec identity (the render key) and its patch identity (the patch key). Two
/// packages with equal manifests were produced by the same inputs; a differing key pinpoints
/// WHICH figure changed and WHETHER it was the data-derived spec or the user's styling.
/// </summary>
public sealed class ExportManifest
{
    [JsonPropertyName("manifestVersion")] public int ManifestVersion { get; set; } = 1;

    [JsonPropertyName("projectId")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("projectTitle")] public string ProjectTitle { get; set; } = "";

    [JsonPropertyName("exportedAtUtc")] public DateTime ExportedAtUtc { get; set; }

    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = "";
    [JsonPropertyName("rendererVersion")] public string RendererVersion { get; set; } = "";
    [JsonPropertyName("chartsStudioSchemaVersion")] public int ChartsStudioSchemaVersion { get; set; }

    [JsonPropertyName("profileId")] public string ProfileId { get; set; } = "";
    [JsonPropertyName("widthInches")] public double WidthInches { get; set; }
    [JsonPropertyName("heightInches")] public double HeightInches { get; set; }
    [JsonPropertyName("dpi")] public int Dpi { get; set; }

    [JsonPropertyName("formats")] public List<string> Formats { get; set; } = new();

    [JsonPropertyName("figureCount")] public int FigureCount { get; set; }

    [JsonPropertyName("figures")] public List<ExportManifestFigure> Figures { get; set; } = new();
}

public sealed class ExportManifestFigure
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("figureId")] public string FigureId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("chartTypeId")] public string ChartTypeId { get; set; } = "";
    [JsonPropertyName("files")] public List<string> Files { get; set; } = new();

    /// <summary>The spec's visual identity — changes when the data-derived figure changes.</summary>
    [JsonPropertyName("specRenderKey")] public string SpecRenderKey { get; set; } = "";

    /// <summary>The patch identity — changes when the user's styling changes. Empty = unedited.</summary>
    [JsonPropertyName("patchKey")] public string PatchKey { get; set; } = "";
}

using AIFlashcardMaker.ChartsStudio.Domain.Specs;

namespace AIFlashcardMaker.ChartsStudio.Domain.Export;

/// <summary>
/// Charts Studio Phase 5 — one fully decided export, ready to run.
///
/// The plan is assembled by the dialog (or a test) and consumed by the export service. It is
/// complete and inert: everything the export will do is readable here BEFORE anything touches
/// disk, which is what makes the pipeline headless-testable end to end and the preview honest
/// — the preview renders from the same plan the export runs.
/// </summary>
public sealed class ExportPlan
{
    /// <summary>Figures to export, in shelf order. Order defines Figure 1, 2, 3 …</summary>
    public required IReadOnlyList<KeptFigure> Figures { get; init; }

    public required ExportProfile Profile { get; init; }

    /// <summary>At least one. Every figure is written in every chosen format.</summary>
    public required IReadOnlyList<ExportFormat> Formats { get; init; }

    public required ExportNameTemplate NameTemplate { get; init; }

    /// <summary>Destination directory. Created if missing.</summary>
    public required string DestinationDirectory { get; init; }

    /// <summary>Study name for naming templates and the manifest.</summary>
    public required string StudyName { get; init; }

    public required string ProjectId { get; init; }

    /// <summary>
    /// Package layout: figures/ subfolder, captions/ subfolder, manifest.json at the root.
    /// Off = figure files written directly into the destination, nothing else.
    /// </summary>
    public bool AsPackage { get; init; }

    public bool IncludeCaptions { get; init; }
    public bool IncludeManifest { get; init; }

    public bool IsValid => Figures.Count > 0 && Formats.Count > 0
        && !string.IsNullOrWhiteSpace(DestinationDirectory);
}

/// <summary>Progress of a running export, for the dialog's progress bar.</summary>
public sealed class ExportProgress
{
    public required int Completed { get; init; }
    public required int Total { get; init; }
    public required string CurrentLabel { get; init; }
}

/// <summary>Outcome for one figure×format write.</summary>
public sealed class ExportItemResult
{
    public required string FigureId { get; init; }
    public required string Label { get; init; }
    public required bool Success { get; init; }
    public string FilePath { get; init; } = "";
    public string Error { get; init; } = "";
}

/// <summary>
/// Outcome of the whole run. A batch CONTINUES past individual failures — one broken figure
/// costs one file, never the submission — so success is per item, and the summary says both.
/// </summary>
public sealed class ExportRunResult
{
    public required IReadOnlyList<ExportItemResult> Items { get; init; }
    public required bool WasCancelled { get; init; }
    public string ManifestPath { get; init; } = "";
    public string CaptionsPath { get; init; } = "";

    public int SucceededCount => Items.Count(i => i.Success);
    public int FailedCount => Items.Count(i => !i.Success);
    public bool AllSucceeded => FailedCount == 0 && !WasCancelled;
}

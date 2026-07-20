using System.Text.Json.Serialization;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;

namespace AIFlashcardMaker.ChartsStudio.Infrastructure.Persistence;

/// <summary>
/// Charts Studio Phase 1 — everything Charts Studio persists for ONE research project.
///
/// PER-PROJECT SIDECAR, NOT A SINGLE GLOBAL FILE.
/// Each research project gets its own file under the Charts Studio data folder. Three reasons
/// this is the right shape, and they get stronger as the module grows:
///
///   1. FAULT ISOLATION. A corrupt or half-written file costs one project's figures. With a
///      single global file it would cost every project's figures at once.
///
///   2. WRITE SCOPE. Figure specs, user patches and history are the bulk of what this module
///      will eventually store. A global file would be rewritten in full on every single edit
///      to any figure in any project. Per-project files keep each write proportional to what
///      actually changed.
///
///   3. LIFECYCLE. Deleting a research project becomes deleting one file, with no read-modify-
///      write of a shared document and no orphan entries left behind in it.
///
/// There is deliberately NO global Charts Studio file. The only app-level fact worth keeping —
/// which project was open last — is DERIVED from the newest LastOpenedAt across these files
/// (see ChartsStudioStore.ResolveLastOpenedProjectId). The picker has to read every file anyway
/// to show figure counts, so deriving it costs nothing and removes a whole file, and with it a
/// whole class of desynchronisation between "the index" and "the truth".
///
/// SCHEMA VERSIONING FROM DAY ONE.
/// Figure collections written by this version must still open several versions from now, so the
/// version stamp exists before there is anything to migrate — rather than being retrofitted
/// once it is already too late to tell old files apart.
/// </summary>
public sealed class ChartsStudioProjectState
{
    /// <summary>
    /// Bumped whenever the persisted shape changes in a way older readers cannot handle.
    ///   v1 = Phase 1/2: figures were bare FigureSpec objects.
    ///   v2 = Phase 3/4: figures are KeptFigure records (spec + patch + metadata).
    /// The store migrates v1 files on read (see ChartsStudioStore.MigrateV1) — the version
    /// stamp exists precisely so this moment could be handled instead of guessed at.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// Identifies these files as Charts Studio's, independently of where they happen to live.
    /// Cheap insurance: a file that does not carry this marker is not treated as ours, so a
    /// stray or mis-copied JSON file can never be parsed as project state and then overwritten.
    /// </summary>
    public const string FileKind = "orbitlab.chartsstudio.project";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Deliberately defaults to EMPTY, not to <see cref="FileKind"/>.
    ///
    /// A C# property initializer would defeat the whole check: a JSON file that simply omits
    /// "kind" would deserialize with the marker already set and be indistinguishable from one
    /// we wrote. Defaulting to empty means only a file that actually carries the marker is
    /// treated as ours. The value is stamped on save (see ChartsStudioStore.Save) and by
    /// <see cref="CreateNew"/> for in-memory states.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>
    /// The RESEARCH project's identifier, stored INSIDE the file as well as being its name.
    ///
    /// Charts Studio deliberately does not own a project concept of its own — a second project
    /// entity would mean two identities for one thing, with synchronisation problems and
    /// orphaned records. Recording the id in the content as well as the filename means a
    /// renamed, copied or hand-moved file is still identifiable, and a mismatch is detectable.
    /// </summary>
    [JsonPropertyName("projectId")]
    public string ProjectId { get; set; } = "";

    /// <summary>When this project was last opened inside Charts Studio.</summary>
    [JsonPropertyName("lastOpenedAt")]
    public DateTime? LastOpenedAt { get; set; }

    /// <summary>
    /// The context fingerprint observed the last time this project was opened here. When a
    /// later phase reopens saved figures, a differing fingerprint is what marks them stale.
    /// Recorded from Phase 1 so staleness works for the very first figures ever saved, rather
    /// than needing a retroactive backfill.
    /// </summary>
    [JsonPropertyName("lastFingerprint")]
    public string? LastFingerprint { get; set; }

    /// <summary>
    /// The figures the user has KEPT for this project, in SHELF ORDER — list position IS the
    /// persisted ordering, so reordering the shelf is reordering this list and nothing more.
    ///
    /// Each entry is a KeptFigure: the immutable recommendation spec, the user's patch overlay
    /// and shelf metadata. The recommendation data exists exactly once (in the spec); the
    /// patch stores only deviations, so nothing is ever duplicated between the two.
    ///
    /// Only kept figures are persisted. Proposals are derived state, reproducible from the
    /// context plus the engine version; storing them would mean migrating records the user
    /// never chose to keep.
    /// </summary>
    [JsonPropertyName("figures")]
    public List<KeptFigure> Figures { get; set; } = new();

    /// <summary>Figures saved for this project.</summary>
    [JsonIgnore]
    public int FigureCount => Figures?.Count ?? 0;

    /// <summary>
    /// Set by the store when the file on disk was written by a NEWER schema version than this
    /// build understands. Such a file is used read-only and never overwritten, because silently
    /// rewriting it with an older shape would destroy data belonging to a version the user may
    /// still return to. Not persisted — it describes this session, not the file.
    /// </summary>
    [JsonIgnore]
    public bool IsReadOnly { get; set; }

    /// <summary>True when this state came from a real file rather than being defaulted.</summary>
    [JsonIgnore]
    public bool ExistsOnDisk { get; set; }

    public static ChartsStudioProjectState CreateNew(string projectId) => new()
    {
        ProjectId = projectId,
        SchemaVersion = CurrentSchemaVersion,
        Kind = FileKind
    };
}

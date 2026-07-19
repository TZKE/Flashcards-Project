using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;

namespace AIFlashcardMaker.ChartsStudio.Infrastructure.Persistence;

/// <summary>
/// Charts Studio Phase 1 — loads and saves ONE SIDECAR FILE PER RESEARCH PROJECT.
///
/// Files live in a dedicated subfolder beside the app's other data files:
///
///     %APPDATA%\AIFlashcardMaker\ChartsStudio\&lt;projectId&gt;.json
///
/// A subfolder rather than a name prefix, so Charts Studio's files never crowd the folder that
/// holds research_projects.json and can be inspected, backed up or cleared as a unit.
///
/// DESIGN RULES THIS TYPE ENFORCES
///
/// 1. FAULT ISOLATION, TWICE OVER. Charts Studio never writes through the same path as
///    research_projects.json, so nothing here can damage the user's research; and because each
///    project has its own file, a failure is contained to a single project's figures.
///
/// 2. LOADING NEVER THROWS. Missing, empty, truncated, corrupt or foreign files all return a
///    usable default state. Charts Studio losing its own preferences is a minor annoyance;
///    Charts Studio refusing to open because of its own file would be a serious one.
///
/// 3. WRITES ARE ATOMIC. Content goes to a temp file and is swapped into place, so a crash or
///    power loss mid-write can never leave a half-written sidecar. A .bak is kept as the
///    last-known-good copy.
///
/// 4. FORWARD-COMPATIBLE READS. A file written by a NEWER schema version is parsed as far as
///    its version stamp and then left strictly alone — never rewritten. Silently downgrading a
///    newer file would destroy data belonging to a version the user may still go back to.
///
/// 5. PATHS ARE NEVER BUILT FROM UNVALIDATED INPUT. See ResolveFileName.
/// </summary>
public sealed class ChartsStudioStore
{
    private readonly string _directory;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Subfolder, inside the app data directory, that holds every project sidecar.</summary>
    public const string FolderName = "ChartsStudio";

    private const string Extension = ".json";

    /// <param name="dataDirectory">
    /// The application data directory — the same folder that holds research_projects.json.
    /// Passed in rather than resolved here so the store is testable against a temp folder.
    /// </param>
    public ChartsStudioStore(string dataDirectory)
    {
        _directory = Path.Combine(dataDirectory, FolderName);
    }

    /// <summary>The folder holding project sidecars, for diagnostics.</summary>
    public string Directory_ => _directory;

    /// <summary>
    /// Set when the last operation hit a file it could not use. Surfaced for diagnostics; the
    /// caller still receives a usable state.
    /// </summary>
    public string? LastLoadIssue { get; private set; }

    // ---------------------------------------------------------------------------------
    // Paths
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Maps a project id to a file name, defensively.
    ///
    /// Research project ids are contractually 8–64 characters of [A-Za-z0-9-] (see
    /// ProjectIdNormalizer), which is already filename-safe — but a path is never built from an
    /// id without checking, because an id that reached disk unvalidated is a directory-traversal
    /// waiting to happen. Anything that is not plain ASCII letters, digits or '-' is replaced by
    /// a deterministic hash of the id, so every project still gets exactly one stable file and
    /// no input can ever escape the folder.
    /// </summary>
    private static string ResolveFileName(string projectId)
    {
        bool safe = projectId.Length is > 0 and <= 64;

        if (safe)
        {
            foreach (char c in projectId)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                       || (c >= '0' && c <= '9') || c == '-';
                if (!ok) { safe = false; break; }
            }
        }

        if (safe) return projectId + Extension;

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(projectId));
        var sb = new StringBuilder(64);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return "id-" + sb.ToString() + Extension;
    }

    private string PathFor(string projectId) => Path.Combine(_directory, ResolveFileName(projectId));

    // ---------------------------------------------------------------------------------
    // Load
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Reads one project's state. Always returns a usable object — never throws, never null.
    /// A project with no sidecar yet gets a fresh default whose ExistsOnDisk is false.
    /// </summary>
    public ChartsStudioProjectState Load(string projectId)
    {
        LastLoadIssue = null;

        if (string.IsNullOrEmpty(projectId))
            return ChartsStudioProjectState.CreateNew("");

        string path = PathFor(projectId);

        try
        {
            if (!File.Exists(path)) return ChartsStudioProjectState.CreateNew(projectId);

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return ChartsStudioProjectState.CreateNew(projectId);

            var state = ParseState(json, projectId, out string? issue);
            LastLoadIssue = issue;

            return state ?? ChartsStudioProjectState.CreateNew(projectId);
        }
        catch (Exception ex)
        {
            LastLoadIssue = $"Charts Studio state could not be read ({ex.GetType().Name}); starting fresh.";
            return ChartsStudioProjectState.CreateNew(projectId);
        }
    }

    /// <summary>
    /// Parses one sidecar's JSON: version check, kind check, and the v1→v2 migration. Shared
    /// by Load and LoadAll so the two paths can never disagree about what a file means.
    /// Returns null for content that should be treated as absent (unparseable).
    /// </summary>
    private static ChartsStudioProjectState? ParseState(string json, string projectId, out string? issue)
    {
        issue = null;

        int version;
        string kind;
        using (var doc = JsonDocument.Parse(json))
        {
            version = doc.RootElement.TryGetProperty("schemaVersion", out var v) ? v.GetInt32() : 0;
            kind = doc.RootElement.TryGetProperty("kind", out var k) ? (k.GetString() ?? "") : "";
        }

        // Rule 4: a newer file is used read-only and never rewritten.
        if (version > ChartsStudioProjectState.CurrentSchemaVersion)
        {
            issue = $"Charts Studio state for this project was written by a newer version of " +
                    $"OrbitLab (schema {version}). It will not be modified.";

            return new ChartsStudioProjectState
            {
                ProjectId = projectId,
                SchemaVersion = version,
                IsReadOnly = true,
                ExistsOnDisk = true
            };
        }

        // A file that does not identify itself as ours is not treated as ours.
        if (!string.Equals(kind, ChartsStudioProjectState.FileKind, StringComparison.Ordinal))
        {
            issue = "A file in the Charts Studio folder was not recognised and was ignored.";
            return new ChartsStudioProjectState
            {
                ProjectId = projectId,
                IsReadOnly = true,      // never overwrite something we did not write
                ExistsOnDisk = true
            };
        }

        var state = version <= 1
            ? MigrateV1(json)
            : JsonSerializer.Deserialize<ChartsStudioProjectState>(json);

        if (state is null)
        {
            issue = "Charts Studio state for this project could not be read; starting fresh.";
            return null;
        }

        state.Figures ??= new List<KeptFigure>();
        state.Figures.RemoveAll(f => f?.Spec is null || string.IsNullOrEmpty(f.Spec.Id));
        state.ExistsOnDisk = true;

        // The filename is authoritative for identity; a mismatched inner id is repaired
        // rather than trusted, so a copied file cannot masquerade as another project.
        state.ProjectId = projectId;

        return state;
    }

    /// <summary>The v1 shape, used only for migration. In v1 a figure was a bare FigureSpec.</summary>
    private sealed class V1State
    {
        [System.Text.Json.Serialization.JsonPropertyName("lastOpenedAt")] public DateTime? LastOpenedAt { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("lastFingerprint")] public string? LastFingerprint { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("figures")] public List<FigureSpec>? Figures { get; set; }
    }

    /// <summary>
    /// v1 → v2: each bare spec becomes a KeptFigure with no patch — exactly what those figures
    /// were: kept recommendations the user had not yet edited. Nothing is lost, nothing is
    /// invented, and the file upgrades to v2 on its next save.
    /// </summary>
    private static ChartsStudioProjectState? MigrateV1(string json)
    {
        var v1 = JsonSerializer.Deserialize<V1State>(json);
        if (v1 is null) return null;

        return new ChartsStudioProjectState
        {
            LastOpenedAt = v1.LastOpenedAt,
            LastFingerprint = v1.LastFingerprint,
            Figures = (v1.Figures ?? new List<FigureSpec>())
                .Where(s => s is not null && !string.IsNullOrEmpty(s.Id))
                .Select(s => new KeptFigure
                {
                    Spec = s,
                    Patch = null,
                    CreatedAt = s.CreatedAt,
                    LastEditedAt = null
                })
                .ToList()
        };
    }

    /// <summary>
    /// Reads every project sidecar, keyed by project id.
    ///
    /// Used by the picker, which needs each project's figure count. Reading the folder is also
    /// what makes a global index file unnecessary — the files ARE the index.
    /// </summary>
    public IReadOnlyDictionary<string, ChartsStudioProjectState> LoadAll()
    {
        var map = new Dictionary<string, ChartsStudioProjectState>(StringComparer.Ordinal);

        try
        {
            if (!Directory.Exists(_directory)) return map;

            foreach (string path in Directory.EnumerateFiles(_directory, "*" + Extension))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    // The inner projectId keys the map; for hashed filenames it is the only
                    // recoverable identity. Files without one are not project sidecars.
                    string? innerId;
                    using (var doc = JsonDocument.Parse(json))
                        innerId = doc.RootElement.TryGetProperty("projectId", out var p) ? p.GetString() : null;
                    if (string.IsNullOrWhiteSpace(innerId)) continue;

                    var state = ParseState(json, innerId, out _);
                    if (state is null) continue;

                    // Foreign files (wrong kind marker) are excluded from the listing entirely;
                    // newer-schema files are listed read-only so the project still shows up.
                    if (state.IsReadOnly &&
                        state.SchemaVersion <= ChartsStudioProjectState.CurrentSchemaVersion)
                        continue;

                    map[state.ProjectId] = state;
                }
                catch
                {
                    // One unreadable sidecar must not hide every other project's figures.
                }
            }
        }
        catch (Exception ex)
        {
            LastLoadIssue = $"Charts Studio folder could not be read ({ex.GetType().Name}).";
        }

        return map;
    }

    /// <summary>
    /// The project opened most recently, DERIVED from the sidecars rather than stored in a
    /// global index. Ties break on project id so the answer is deterministic.
    /// </summary>
    public string? ResolveLastOpenedProjectId() => ResolveLastOpenedProjectId(LoadAll());

    /// <summary>Overload for callers that already hold the map, to avoid a second folder read.</summary>
    public static string? ResolveLastOpenedProjectId(
        IReadOnlyDictionary<string, ChartsStudioProjectState> states)
    {
        return states.Values
            .Where(s => s.LastOpenedAt.HasValue)
            .OrderByDescending(s => s.LastOpenedAt!.Value)
            .ThenBy(s => s.ProjectId, StringComparer.Ordinal)
            .Select(s => s.ProjectId)
            .FirstOrDefault();
    }

    // ---------------------------------------------------------------------------------
    // Save
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Writes one project's sidecar atomically. Returns false when the write could not be
    /// completed; callers treat that as non-fatal, because losing Charts Studio state must
    /// never block the user's work.
    /// </summary>
    public bool Save(ChartsStudioProjectState state)
    {
        if (state is null) return false;
        if (state.IsReadOnly) return false;                       // Rule 4
        if (string.IsNullOrEmpty(state.ProjectId)) return false;

        string path = PathFor(state.ProjectId);
        string tempPath = path + ".tmp";
        string backupPath = path + ".bak";

        try
        {
            Directory.CreateDirectory(_directory);

            state.SchemaVersion = ChartsStudioProjectState.CurrentSchemaVersion;
            state.Kind = ChartsStudioProjectState.FileKind;

            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, WriteOptions));

            if (File.Exists(path))
            {
                // Replace keeps a last-known-good backup and swaps in one operation.
                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }

            state.ExistsOnDisk = true;
            return true;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
            return false;
        }
    }
}

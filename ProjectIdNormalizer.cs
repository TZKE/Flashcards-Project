using System;
using System.Collections.Generic;
using System.Linq;

namespace AIFlashcardMaker;

/// <summary>
/// Phase 9 (integrity hardening): keeps every research project's opaque <c>Id</c> a
/// stable, valid, unique server identity. This runs on EVERY project load — it is not a
/// one-shot migration gated by a marker file — so an old backup imported at any time is
/// always repaired, and repeated launches never disturb already-valid ids.
///
/// Rules (mirror the backend's project-id validation exactly, so any id we keep or mint
/// is guaranteed to be accepted by the reservation endpoints):
///   • valid = 8–64 chars, each a letter/digit or '-'
///   • empty / whitespace / malformed / DUPLICATE ids are replaced with a fresh GUID
///   • valid, unique ids are left byte-for-byte unchanged
///
/// The function is pure (no I/O) and deterministic given its RNG, so it is unit-tested in
/// isolation. Persisting the result is the caller's responsibility (atomic write).
/// </summary>
public static class ProjectIdNormalizer
{
    /// <summary>Backend contract: 8–64 chars of [A-Za-z0-9-]. Must match ProjectUsageEndpoints.IsValidProjectId.</summary>
    public static bool IsValidProjectId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id!.Length is >= 8 and <= 64
        && id.All(c => char.IsLetterOrDigit(c) || c == '-');

    /// <summary>
    /// Repairs ids in place. Returns true if any id was changed (caller should then persist).
    /// The first occurrence of a valid id wins; later duplicates and all invalid ids get a
    /// fresh distinct GUID. Never changes a valid, unique id.
    /// </summary>
    public static bool NormalizeProjectIds(IReadOnlyList<ResearchProject> projects)
    {
        if (projects is null) return false;
        bool changed = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in projects)
        {
            if (p is null) continue;
            if (IsValidProjectId(p.Id) && seen.Add(p.Id)) continue;   // valid + first time seen → keep
            string fresh;
            do { fresh = Guid.NewGuid().ToString("N"); } while (!seen.Add(fresh));
            p.Id = fresh;
            changed = true;
        }
        return changed;
    }
}

using System;
using System.Threading.Tasks;

namespace AIFlashcardMaker;

/// <summary>
/// A first-run descriptive-statistics result that was computed locally but whose backend
/// FINALIZATION has not yet succeeded (transient network at finalize time, or a crash
/// before it completed). Persisted locally and retried automatically until it either
/// activates (finalizes) or is proven unrecoverable (the cycle's allowance is now full).
///
/// PRIVACY: <see cref="Result"/> is stored on THIS MACHINE ONLY, exactly like the project's
/// own research file. Nothing in this record is ever uploaded — the reserve/finalize calls
/// send only the opaque project id + reservation id. The result is kept here (not written
/// into the project) specifically so it is NOT exposed/committed as a finished result until
/// the server has actually accounted for the project.
/// </summary>
public sealed class PendingFinalization
{
    public string ProjectId { get; set; } = "";
    public string ReservationId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The on-hold result to activate once finalization succeeds. Local only; never uploaded.</summary>
    public DescriptiveStatisticsRecord? Result { get; set; }

    /// <summary>Terminal: the slot can no longer be obtained in the current cycle (allowance full).</summary>
    public bool Unrecoverable { get; set; }
    public string? UnrecoverableReason { get; set; }
}

public enum PendingRecoveryOutcome
{
    /// <summary>The project is now permanently counted server-side — commit the stored result.</summary>
    Committed,
    /// <summary>Transient/failed this attempt — keep the record and retry later.</summary>
    StillPending,
    /// <summary>Capacity is gone — do not commit; let the user discard the result.</summary>
    Unrecoverable,
}

/// <summary>
/// The recovery algorithm for a single <see cref="PendingFinalization"/>. Pure and
/// dependency-injected (finalize/reserve delegates) so it is unit-testable without a UI or
/// a live backend. Guarantees, given the backend's idempotent semantics:
///   • never counts a project twice — consumption is unique per (user, project) server-side;
///   • never uploads result data — only opaque ids cross the wire;
///   • never leaves a project permanently stuck — every path returns Committed / StillPending
///     / Unrecoverable, and Unrecoverable is user-discardable.
///
/// Algorithm:
///   1. Finalize the hold we already have. Counted → Committed. Network/other → StillPending.
///      Expired/not-found → fall through.
///   2. Re-acquire capacity for the SAME opaque project:
///        already-counted  → Committed (counted elsewhere/earlier);
///        new reservation  → adopt it and finalize → Committed, else StillPending;
///        limit reached    → Unrecoverable;
///        otherwise        → StillPending (retry later).
/// </summary>
public sealed class PendingFinalizationRecovery
{
    private readonly Func<string, string, Task<ApiResult<FinalizeResultDto>>> _finalize;  // (projectId, reservationId)
    private readonly Func<string, Task<ApiResult<ReserveResultDto>>> _reserve;            // (projectId)

    public PendingFinalizationRecovery(
        Func<string, string, Task<ApiResult<FinalizeResultDto>>> finalize,
        Func<string, Task<ApiResult<ReserveResultDto>>> reserve)
    {
        _finalize = finalize;
        _reserve = reserve;
    }

    public async Task<PendingRecoveryOutcome> ResolveAsync(PendingFinalization entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.ProjectId))
            return PendingRecoveryOutcome.Unrecoverable;   // corrupt/missing metadata → discardable, never stuck

        // 1) Try to finalize the reservation we already hold.
        if (!string.IsNullOrWhiteSpace(entry.ReservationId))
        {
            var fin = await _finalize(entry.ProjectId, entry.ReservationId);
            if (Counted(fin)) return PendingRecoveryOutcome.Committed;
            if (!ExpiredOrMissing(fin)) return PendingRecoveryOutcome.StillPending;   // network / other → retry
            // expired / not-found → fall through to re-acquire
        }

        // 2) The hold is gone — re-acquire capacity for the SAME opaque project.
        var res = await _reserve(entry.ProjectId);
        if (res.Ok && res.Data!.AlreadyCounted) return PendingRecoveryOutcome.Committed;
        if (res.Ok && !string.IsNullOrWhiteSpace(res.Data!.ReservationId))
        {
            entry.ReservationId = res.Data.ReservationId!;   // adopt the fresh hold (idempotent-safe on crash)
            var fin2 = await _finalize(entry.ProjectId, entry.ReservationId);
            return Counted(fin2) ? PendingRecoveryOutcome.Committed : PendingRecoveryOutcome.StillPending;
        }
        if (res.Error is { Code: "project_limit_reached" }) return PendingRecoveryOutcome.Unrecoverable;
        return PendingRecoveryOutcome.StillPending;   // not_entitled / network / other → retry later
    }

    private static bool Counted(ApiResult<FinalizeResultDto> r) =>
        r.Ok && (r.Data!.Finalized || r.Data.AlreadyCounted);

    private static bool ExpiredOrMissing(ApiResult<FinalizeResultDto> r) =>
        r.Error is { } e && (e.Code == "reservation_expired" || e.Code == "reservation_not_found");
}

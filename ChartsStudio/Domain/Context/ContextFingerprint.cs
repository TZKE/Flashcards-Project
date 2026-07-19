using System.Security.Cryptography;
using System.Text;

namespace AIFlashcardMaker.ChartsStudio.Domain.Context;

/// <summary>
/// Charts Studio Phase 1 — content-derived identity for one <see cref="AnalysisContext"/>.
///
/// This is the basis of ALL staleness detection in Charts Studio. Two contexts with the
/// same fingerprint are interchangeable; a mismatch means every figure built from the older
/// one is stale and must be visibly marked (never silently regenerated).
///
/// The fingerprint is computed from the *inputs* that can change a figure:
///   • the variable set and each variable's declared type / level / role
///   • the dataset size the project reports
///   • the identities of saved computed results
///   • the extraction-sheet revision timestamp
///
/// It deliberately does NOT include cosmetic project fields (notes, title, specialty) —
/// renaming a project must not invalidate its figures.
///
/// Determinism matters more than speed here: the same project state must always produce the
/// same fingerprint across runs and machines, so the canonical string below is built in a
/// fixed order with fixed formatting and never depends on dictionary ordering or culture.
/// </summary>
public sealed class ContextFingerprint : IEquatable<ContextFingerprint>
{
    /// <summary>Lowercase hex SHA-256 of the canonical input string.</summary>
    public string Value { get; }

    private ContextFingerprint(string value) => Value = value;

    /// <summary>An explicit "no fingerprint" value — never equal to a real one.</summary>
    public static ContextFingerprint None { get; } = new("");

    public bool HasValue => Value.Length > 0;

    /// <summary>
    /// Builds a fingerprint from an already-assembled canonical description. The caller
    /// (the Research Lab adapter) owns what goes in; this type owns only the hashing, so the
    /// hash algorithm can change later without touching the projection logic.
    /// </summary>
    public static ContextFingerprint FromCanonicalInput(string canonicalInput)
    {
        if (string.IsNullOrEmpty(canonicalInput)) return None;

        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalInput));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return new ContextFingerprint(sb.ToString());
    }

    /// <summary>Short form for display in provenance panels and diagnostics.</summary>
    public string ShortValue => HasValue ? Value[..Math.Min(12, Value.Length)] : "—";

    public bool Equals(ContextFingerprint? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ContextFingerprint);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => HasValue ? Value : "(none)";
}

using AIFlashcardMaker.ChartsStudio.Domain.Specs;
using AIFlashcardMaker.ChartsStudio.Domain.Themes;

namespace AIFlashcardMaker.ChartsStudio.Domain.Validation;

/// <summary>One problem with one field, in words the user can act on.</summary>
public sealed class PatchValidationError
{
    public required string Field { get; init; }
    public required string Message { get; init; }
    public override string ToString() => $"{Field}: {Message}";
}

/// <summary>
/// Charts Studio Phase 3 — validates a patch BEFORE it is committed to history.
///
/// Two layers of defence, deliberately redundant:
///   1. This validator — rejects bad input at the editor with a message, so the user learns
///      what was wrong instead of silently getting something else.
///   2. FigureStyleResolver — clamps and falls back at render time, so a patch that arrived
///      bad anyway (old file, hand-edited JSON) degrades the figure instead of crashing.
///
/// Ranges come from the resolver's shared constants so the two layers can never disagree
/// about what "valid" means.
/// </summary>
public static class FigurePatchValidator
{
    public static IReadOnlyList<PatchValidationError> Validate(FigurePatch? patch)
    {
        var errors = new List<PatchValidationError>();
        if (patch is null) return errors;

        void Range(string field, double? v, double min, double max)
        {
            if (v is null) return;
            if (double.IsNaN(v.Value) || double.IsInfinity(v.Value))
                errors.Add(new PatchValidationError { Field = field, Message = "Not a usable number." });
            else if (v.Value < min || v.Value > max)
                errors.Add(new PatchValidationError { Field = field, Message = $"Must be between {min} and {max}." });
        }

        void Hex(string field, string? v)
        {
            if (v is not null && FigureStyleResolver.ValidHexOrNull(v) is null)
                errors.Add(new PatchValidationError { Field = field, Message = "Use a colour like #2F6FB2." });
        }

        Range("Title size", patch.TitleFontSize, FigureStyleResolver.MinFontSize, FigureStyleResolver.MaxFontSize);
        Range("Axis size", patch.AxisFontSize, FigureStyleResolver.MinFontSize, FigureStyleResolver.MaxFontSize);
        Range("Tick size", patch.TickFontSize, FigureStyleResolver.MinTickFontSize, FigureStyleResolver.MaxTickFontSize);
        Range("Line width", patch.LineWidth, FigureStyleResolver.MinLineWidth, FigureStyleResolver.MaxLineWidth);
        Range("Marker size", patch.MarkerSize, FigureStyleResolver.MinMarkerSize, FigureStyleResolver.MaxMarkerSize);
        Range("Opacity", patch.Opacity, FigureStyleResolver.MinOpacity, FigureStyleResolver.MaxOpacity);

        Hex("Series colour", patch.SeriesColorHex);
        Hex("Background colour", patch.BackgroundColorHex);

        // A whitespace-only override is almost certainly an accident: it would silently fall
        // back to the default title while LOOKING like an override in the patch. Reject it so
        // the user either types a real title or resets the field.
        if (patch.Title is not null && patch.Title.Trim().Length == 0)
            errors.Add(new PatchValidationError { Field = "Title", Message = "A title cannot be blank — reset the field to use the recommended title." });

        if (patch.Orientation is not null
            && !string.Equals(patch.Orientation, "vertical", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(patch.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase))
            errors.Add(new PatchValidationError { Field = "Orientation", Message = "Must be vertical or horizontal." });

        return errors;
    }
}

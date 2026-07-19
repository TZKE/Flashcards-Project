using System.IO;
using System.Windows.Media.Imaging;
using AIFlashcardMaker.ChartsStudio.Domain.Recommendation;
using AIFlashcardMaker.ChartsStudio.Domain.Specs;

namespace AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

/// <summary>Where one card is in its render lifecycle.</summary>
public enum FigureCardState
{
    /// <summary>Queued but not drawn. The card shows its title and reason already.</summary>
    Pending,

    /// <summary>Being drawn.</summary>
    Rendering,

    /// <summary>Drawn; <see cref="FigureCardViewModel.Image"/> is set.</summary>
    Rendered,

    /// <summary>Could not be drawn; <see cref="FigureCardViewModel.FailureReason"/> says why.</summary>
    Failed
}

/// <summary>
/// Charts Studio Phase 2 — one card on the contact sheet.
///
/// The card is meaningful BEFORE its figure exists: title, reason and cautions are text and
/// appear immediately, so the sheet is readable while thumbnails are still drawing. That is the
/// difference between a sheet that feels instant and one that feels like a page load.
/// </summary>
public sealed class FigureCardViewModel : ObservableObject
{
    private FigureCardState _state = FigureCardState.Pending;
    private BitmapSource? _image;
    private string _failureReason = "";
    private bool _isKept;

    public FigureCardViewModel(FigureCandidate candidate)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
    }

    public FigureCandidate Candidate { get; }

    public FigureSpec Spec => Candidate.Spec;

    // ---- Text (available immediately) -----------------------------------------------

    /// <summary>What the figure is ABOUT — the card leads with this, not the chart name.</summary>
    public string Headline => Candidate.HeadlineDisplay;

    /// <summary>Chart form and n, as supporting detail.</summary>
    public string Subtitle => Candidate.SubtitleDisplay;

    public string Rationale => Candidate.Rationale;

    public string BestFor => Candidate.ChartType.BestFor;

    public string AvoidWhen => Candidate.ChartType.AvoidWhen;

    public bool HasCautions => Candidate.HasCautions;

    public string CautionText => string.Join("  ·  ", Candidate.Cautions);

    // ---- Render state ---------------------------------------------------------------

    public FigureCardState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsRendered));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsFailed));
        }
    }

    public bool IsRendered => State == FigureCardState.Rendered;
    public bool IsBusy => State is FigureCardState.Pending or FigureCardState.Rendering;
    public bool IsFailed => State == FigureCardState.Failed;

    public BitmapSource? Image
    {
        get => _image;
        private set => Set(ref _image, value);
    }

    public string FailureReason
    {
        get => _failureReason;
        private set => Set(ref _failureReason, value);
    }

    /// <summary>Whether this figure is already on the shelf. Drives the Keep/Kept affordance.</summary>
    public bool IsKept
    {
        get => _isKept;
        set
        {
            if (!Set(ref _isKept, value)) return;
            OnPropertyChanged(nameof(KeepLabel));
        }
    }

    public string KeepLabel => IsKept ? "Kept" : "Keep";

    // ---- Transitions ----------------------------------------------------------------

    public void MarkRendering() => State = FigureCardState.Rendering;

    /// <summary>
    /// Attaches a rendered image. The bitmap is FROZEN, which is what makes it safe to hand
    /// between the render thread and the UI thread without marshalling every card.
    /// </summary>
    public void SetImage(byte[] pngBytes)
    {
        try
        {
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(pngBytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;   // read fully, then release the stream
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze();

            Image = bitmap;
            FailureReason = "";
            State = FigureCardState.Rendered;
        }
        catch (Exception ex)
        {
            SetFailed($"The figure image could not be read ({ex.GetType().Name}).");
        }
    }

    public void SetFailed(string reason)
    {
        Image = null;
        FailureReason = string.IsNullOrWhiteSpace(reason) ? "This figure could not be drawn." : reason;
        State = FigureCardState.Failed;
    }

    /// <summary>
    /// Returns the card to Pending without clearing its text. Used when a render was superseded
    /// rather than failed — the user should see it retry, not see an error for something that
    /// was simply abandoned.
    /// </summary>
    public void ResetToPending()
    {
        Image = null;
        FailureReason = "";
        State = FigureCardState.Pending;
    }
}

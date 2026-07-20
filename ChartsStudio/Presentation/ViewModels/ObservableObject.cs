using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

/// <summary>
/// Charts Studio Phase 1 — minimal INotifyPropertyChanged base for the module's view models.
///
/// Hand-rolled deliberately. The app takes only two package references (Velopack and
/// ProtectedData), and adding an MVVM toolkit for twenty lines of plumbing would put a new
/// dependency into a self-contained installer that is currently published and SHA-verified.
/// The same Set/OnPropertyChanged shape already exists in ResearchLab.cs's ResearchVariable,
/// so this matches how the codebase already does change notification.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Sets a field and raises change notification only when the value actually changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

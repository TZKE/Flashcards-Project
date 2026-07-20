using System.Windows.Input;

namespace AIFlashcardMaker.ChartsStudio.Presentation.ViewModels;

/// <summary>
/// Charts Studio Phase 3 — minimal ICommand for keyboard bindings (Ctrl+Z/Ctrl+Y/Esc).
///
/// Hand-rolled for the same reason as ObservableObject: the app deliberately takes no MVVM
/// toolkit dependency, and fifteen lines of plumbing do not justify adding one to a published,
/// SHA-verified installer.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    /// <summary>Called by the owner when the underlying condition may have changed.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

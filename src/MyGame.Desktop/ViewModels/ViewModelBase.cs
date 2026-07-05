using CommunityToolkit.Mvvm.ComponentModel;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// Base class for every screen-level view model. Provides the common
/// <see cref="Title"/> (bound to the window title or a header) and
/// <see cref="IsBusy"/> (bound to a busy indicator overlay) properties,
/// plus an optional <see cref="ErrorMessage"/> channel for screen-local
/// errors that should be surfaced inline rather than in a dialog.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
    private string _title = string.Empty;
    private bool _isBusy;
    private string? _errorMessage;

    /// <summary>
    /// Screen title — bound to a header label and (for the root shell)
    /// to the window title.
    /// </summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>
    /// True while a long-running async operation is in flight. Bound to
    /// a busy overlay or a spinner; commands can disable themselves
    /// when this is true (see the per-VM CanExecute pattern).
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        protected set => SetProperty(ref _isBusy, value);
    }

    /// <summary>
    /// Inline error message. Null = no error. ViewModels should clear
    /// this on successful operations and set it on caught exceptions
    /// the user should see inline (network errors, save failures).
    /// </summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        protected set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>Convenience: set the error and return false (so a
    /// command can <c>return Error(...);</c> in a one-liner).</summary>
    protected bool Error(string message)
    {
        ErrorMessage = message;
        return false;
    }

    /// <summary>Clear any previously-set inline error.</summary>
    protected void ClearError() => ErrorMessage = null;

    /// <summary>
    /// Public error-setter for callers that aren't subclasses (e.g. the
    /// shell view model wrapping initialization in a try/catch). The
    /// setter is otherwise protected so subclasses fully own their
    /// error channel.
    /// </summary>
    public void SetError(string message) => ErrorMessage = message;
}

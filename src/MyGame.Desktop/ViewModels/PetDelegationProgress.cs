using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// One row in the world-build progress dialog's «Pet-агенты» section
/// (issue #76). Tracks the live status of a single
/// <c>PetDelegation</c> the orchestrator runs after the deterministic
/// committer stage. Bound to the WorldBuildView's ItemsControl; each row
/// shows the delegation label, a status glyph (▶ running / ✓ done / ✗
/// error), and either a summary (when done) or an error message (when
/// failed).
/// </summary>
/// <remarks>
/// The orchestrator reports progress via
/// <c>IProgress&lt;WorldBuildProgress&gt;</c>; the
/// <see cref="WorldBuildViewModel"/> translates each
/// <c>Stage == "pet"</c> event into an update on the matching
/// <see cref="PetDelegationProgress"/> entry (matched by the delegation
/// label embedded in the progress event's Label field).
/// </remarks>
public sealed class PetDelegationProgress : ObservableObject
{
    /// <summary>
    /// Display label of the delegation (matches
    /// <c>PetDelegation.Label</c>). Set at construction and never
    /// changes; the orchestrator includes it in every pet-stage progress
    /// event so the VM can route updates to the right row.
    /// </summary>
    public string Label { get; set; } = "";

    private string _status = "pending"; // pending | running | done | error
    /// <summary>
    /// Current status of this delegation. One of:
    /// <list type="bullet">
    ///   <item><c>pending</c> — pre-populated; the orchestrator hasn't
    ///     started this delegation yet.</item>
    ///   <item><c>running</c> — the orchestrator reported Active for this
    ///     delegation.</item>
    ///   <item><c>done</c> — the orchestrator reported Done; see
    ///     <see cref="Summary"/> for what it accomplished.</item>
    ///   <item><c>error</c> — the orchestrator reported Error; see
    ///     <see cref="Error"/> for the failure message.</item>
    /// </list>
    /// </summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    private string? _summary;
    /// <summary>
    /// Short summary of what the delegation accomplished (set when
    /// <see cref="Status"/> becomes <c>done</c>). Bound to a muted
    /// TextBlock under the row's status glyph.
    /// </summary>
    public string? Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    private string? _error;
    /// <summary>
    /// Error message (set when <see cref="Status"/> becomes
    /// <c>error</c>). Bound to a danger-colored TextBlock under the row's
    /// status glyph.
    /// </summary>
    public string? Error
    {
        get => _error;
        set => SetProperty(ref _error, value);
    }

    // ─── Convenience flags for XAML bindings ────────────────────────

    /// <summary>True when <see cref="Status"/> == "running".</summary>
    public bool IsRunning => Status == "running";
    /// <summary>True when <see cref="Status"/> == "done".</summary>
    public bool IsDone => Status == "done";
    /// <summary>True when <see cref="Status"/> == "error".</summary>
    public bool IsError => Status == "error";
    /// <summary>True when <see cref="Status"/> == "pending".</summary>
    public bool IsPending => Status == "pending";

    /// <summary>
    /// Update <see cref="Status"/> and refresh the derived Is* flags so
    /// the XAML bindings re-evaluate the status glyph + color.
    /// </summary>
    public void SetStatus(string newStatus)
    {
        Status = newStatus;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsPending));
    }
}

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MyGame.Core.AI.Agents;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// One editable row in the WorldBrief screen's pet-delegation list
/// (issue #22). Wraps a <see cref="PetDelegation"/> with two-way
/// bindable properties (Label, Task, MaxIterations) so the user can
/// edit the delegation inline. The WorldBriefViewModel.Build command
/// reads the final values via <see cref="ToDelegation"/> when
/// assembling the orchestrator's delegation list.
/// </summary>
/// <remarks>
/// Pre-populated at construction with the matching
/// <see cref="PetDelegation"/> field values; changes propagate
/// immediately to the bound UI controls (TextBox / NumericUpDown).
/// </remarks>
public sealed class PetDelegationViewModel : ObservableObject
{
    private string _label = string.Empty;
    private string _task = string.Empty;
    private int _maxIterations = 6;

    /// <summary>
    /// Create a row view-model from an existing
    /// <see cref="PetDelegation"/>. The row copies the delegation's
    /// fields; subsequent edits to the row don't mutate the original
    /// delegation (so the user can cancel their edits by closing the
    /// screen without building).
    /// </summary>
    public PetDelegationViewModel(PetDelegation delegation)
    {
        _label = delegation?.Label ?? string.Empty;
        _task = delegation?.Task ?? string.Empty;
        _maxIterations = delegation?.MaxIterations ?? 6;
    }

    /// <summary>
    /// Display name shown in the orchestrator's progress UI
    /// ("Pet: {Label}"). Bound to a TextBox in the editor row.
    /// </summary>
    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    /// <summary>
    /// The task description handed to the pet agent. Should be concrete
    /// and self-contained — the pet agent doesn't see the orchestrator's
    /// other context. Bound to a multi-line TextBox in the editor row.
    /// </summary>
    public string Task
    {
        get => _task;
        set => SetProperty(ref _task, value);
    }

    /// <summary>
    /// Iteration cap for this delegation (1..20). Bound to a
    /// NumericUpDown in the editor row. Defaults to 6 (matches
    /// <see cref="PetAgent.DefaultMaxIterations"/>).
    /// </summary>
    public int MaxIterations
    {
        get => _maxIterations;
        set => SetProperty(ref _maxIterations, value);
    }

    /// <summary>
    /// Build a fresh <see cref="PetDelegation"/> from the row's current
    /// values. Called by the WorldBriefViewModel.Build command when
    /// assembling the orchestrator's delegation list. The Settings field
    /// is left null (per-pet AI overrides aren't exposed in the brief UI
    /// — they're an advanced feature handled elsewhere).
    /// </summary>
    public PetDelegation ToDelegation() => new()
    {
        Label = string.IsNullOrWhiteSpace(Label) ? "(без названия)" : Label.Trim(),
        Task = Task ?? string.Empty,
        MaxIterations = Math.Max(1, Math.Min(20, MaxIterations)),
    };
}

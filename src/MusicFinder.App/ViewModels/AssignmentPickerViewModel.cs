using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicFinder.Application.Services;

namespace MusicFinder.App.ViewModels;

/// <summary>
/// One row of a "+" popup. IsChecked is true when every selected track has it, false when none does,
/// and null (shown as a dash) when only some do. Clicking a mixed row assigns it to all selected tracks.
/// </summary>
public sealed partial class AssignmentChoice : ObservableObject
{
    private readonly Func<AssignmentChoice, bool, Task> _apply;
    private bool _silent;

    public long Id { get; }
    public string Label { get; }
    public int Depth { get; init; }
    public bool IsParent => Depth == 0;

    [ObservableProperty] private bool? _isChecked = false;

    public AssignmentChoice(long id, string label, Func<AssignmentChoice, bool, Task> apply)
    {
        Id = id;
        Label = label;
        _apply = apply;
    }

    async partial void OnIsCheckedChanged(bool? value)
    {
        if (_silent || value is null) return;
        await _apply(this, value.Value);
    }

    public void SetState(bool? state)
    {
        _silent = true;
        try { IsChecked = state; }
        finally { _silent = false; }
    }
}

/// <summary>State and filtering for a single assignment popup (DJ tags or the user's genre assignments).</summary>
public sealed partial class AssignmentPickerViewModel : ObservableObject
{
    public ObservableCollection<AssignmentChoice> Choices { get; } = [];

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _isEnabled;

    /// <summary>Normalized containment match used by the popup's list filter.</summary>
    public bool Matches(AssignmentChoice choice)
    {
        var needle = TextNormalizer.Normalize(FilterText);
        return needle.Length == 0 || TextNormalizer.Normalize(choice.Label).Contains(needle, StringComparison.Ordinal);
    }

    public event EventHandler? FilterChanged;

    partial void OnFilterTextChanged(string value) => FilterChanged?.Invoke(this, EventArgs.Empty);
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace MusicFinder.App.ViewModels;

public enum FilterKind { Normal, UnknownGenre, Uncategorized }

/// <summary>One checkable entry in a filter dropdown (taxonomy genre, DJ tag, decade or BPM range).</summary>
public sealed partial class FilterOption : ObservableObject
{
    public string Label { get; }
    /// <summary>Genre/tag id for taxonomy filters; unused for decades and BPM ranges.</summary>
    public long Id { get; init; }
    public double Min { get; }
    public double Max { get; }
    /// <summary>Indentation level in the popup (0 = top-level genre).</summary>
    public int Depth { get; init; }
    public FilterKind Kind { get; init; }
    /// <summary>Track count shown next to the label, or empty.</summary>
    public string CountText { get; init; } = "";
    /// <summary>True for the pinned "Unknown genre" / "Uncategorized" entries (drawn above the taxonomy).</summary>
    public bool IsVirtual => Kind != FilterKind.Normal;
    public bool IsParent => Depth == 0 && !IsVirtual;

    [ObservableProperty] private bool _isSelected;

    public event EventHandler? Toggled;

    public FilterOption(string label, double min = 0, double max = 0)
    {
        Label = label;
        Min = min;
        Max = max;
    }

    private bool _silent;

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_silent) Toggled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the state without raising <see cref="Toggled"/>.</summary>
    public void SetSilently(bool value)
    {
        _silent = true;
        try { IsSelected = value; }
        finally { _silent = false; }
    }
}

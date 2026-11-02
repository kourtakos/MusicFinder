using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MusicFinder.App.ViewModels;

namespace MusicFinder.App.Controls;

/// <summary>A round "+" button that opens a checklist for assigning tags or genres to the selected tracks.</summary>
public partial class AssignmentPicker : UserControl
{
    private const int SearchThreshold = 8;

    public static readonly DependencyProperty PickerProperty =
        DependencyProperty.Register(nameof(Picker), typeof(AssignmentPickerViewModel), typeof(AssignmentPicker),
            new PropertyMetadata(null, (d, e) => ((AssignmentPicker)d).OnPickerChanged(e.OldValue as AssignmentPickerViewModel, e.NewValue as AssignmentPickerViewModel)));

    public static readonly DependencyProperty TipProperty =
        DependencyProperty.Register(nameof(Tip), typeof(string), typeof(AssignmentPicker), new PropertyMetadata(""));

    public static readonly DependencyProperty HasSearchProperty =
        DependencyProperty.Register(nameof(HasSearch), typeof(bool), typeof(AssignmentPicker), new PropertyMetadata(false));

    public AssignmentPickerViewModel? Picker { get => (AssignmentPickerViewModel?)GetValue(PickerProperty); set => SetValue(PickerProperty, value); }
    public string Tip { get => (string)GetValue(TipProperty); set => SetValue(TipProperty, value); }
    public bool HasSearch { get => (bool)GetValue(HasSearchProperty); private set => SetValue(HasSearchProperty, value); }

    public AssignmentPicker()
    {
        InitializeComponent();
        Popup.Closed += (_, _) => { if (Picker is { } p) p.FilterText = ""; };
    }

    private void OnPickerChanged(AssignmentPickerViewModel? oldValue, AssignmentPickerViewModel? newValue)
    {
        if (oldValue is not null) oldValue.FilterChanged -= OnFilterChanged;
        if (newValue is null) return;

        newValue.FilterChanged += OnFilterChanged;
        newValue.Choices.CollectionChanged += (_, _) => HasSearch = newValue.Choices.Count > SearchThreshold;
        HasSearch = newValue.Choices.Count > SearchThreshold;
        CollectionViewSource.GetDefaultView(newValue.Choices).Filter = o => o is AssignmentChoice c && newValue.Matches(c);
    }

    private void OnFilterChanged(object? sender, EventArgs e)
    {
        if (Picker is { } p) CollectionViewSource.GetDefaultView(p.Choices).Refresh();
    }
}

using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MusicFinder.App.ViewModels;
using MusicFinder.Application.Services;
using MusicFinder.App.Localization;

namespace MusicFinder.App.Controls;

/// <summary>Pill-shaped multi-select dropdown. The label shows how many options are active.</summary>
public partial class FilterDropDown : UserControl
{
    /// <summary>Lists longer than this get a filter box.</summary>
    private const int SearchThreshold = 15;

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(FilterDropDown),
            new PropertyMetadata("", (d, _) => ((FilterDropDown)d).UpdateText()));

    public static readonly DependencyProperty OptionsProperty =
        DependencyProperty.Register(nameof(Options), typeof(IEnumerable), typeof(FilterDropDown),
            new PropertyMetadata(null, (d, e) => ((FilterDropDown)d).OnOptionsChanged(e.OldValue as IEnumerable, e.NewValue as IEnumerable)));

    public static readonly DependencyProperty ToggleTextProperty =
        DependencyProperty.Register(nameof(ToggleText), typeof(string), typeof(FilterDropDown), new PropertyMetadata(""));

    public static readonly DependencyProperty HasOptionsProperty =
        DependencyProperty.Register(nameof(HasOptions), typeof(bool), typeof(FilterDropDown), new PropertyMetadata(false));

    public static readonly DependencyProperty HasSearchProperty =
        DependencyProperty.Register(nameof(HasSearch), typeof(bool), typeof(FilterDropDown), new PropertyMetadata(false));

    public static readonly DependencyProperty FilterTextProperty =
        DependencyProperty.Register(nameof(FilterText), typeof(string), typeof(FilterDropDown),
            new PropertyMetadata("", (d, _) => ((FilterDropDown)d).ApplyFilter()));

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public IEnumerable? Options { get => (IEnumerable?)GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }
    public string ToggleText { get => (string)GetValue(ToggleTextProperty); private set => SetValue(ToggleTextProperty, value); }
    public bool HasOptions { get => (bool)GetValue(HasOptionsProperty); private set => SetValue(HasOptionsProperty, value); }
    public bool HasSearch { get => (bool)GetValue(HasSearchProperty); private set => SetValue(HasSearchProperty, value); }
    public string FilterText { get => (string)GetValue(FilterTextProperty); set => SetValue(FilterTextProperty, value); }

    public FilterDropDown()
    {
        InitializeComponent();
        UpdateText();
        Popup.Closed += (_, _) => FilterText = ""; // start fresh next time
    }

    private void OnOptionsChanged(IEnumerable? oldValue, IEnumerable? newValue)
    {
        if (oldValue is INotifyCollectionChanged oldCollection) oldCollection.CollectionChanged -= OnCollectionChanged;
        if (oldValue is not null) foreach (var o in oldValue.OfType<FilterOption>()) o.PropertyChanged -= OnOptionChanged;
        if (newValue is INotifyCollectionChanged newCollection) newCollection.CollectionChanged += OnCollectionChanged;
        if (newValue is not null) foreach (var o in newValue.OfType<FilterOption>()) o.PropertyChanged += OnOptionChanged;
        ApplyFilter();
        UpdateText();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (var o in e.OldItems.OfType<FilterOption>()) o.PropertyChanged -= OnOptionChanged;
        if (e.NewItems is not null) foreach (var o in e.NewItems.OfType<FilterOption>()) o.PropertyChanged += OnOptionChanged;
        UpdateText();
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterOption.IsSelected)) UpdateText();
    }

    /// <summary>Case- and accent-insensitive containment filter over the option labels.</summary>
    private void ApplyFilter()
    {
        if (Options is null) return;
        var view = CollectionViewSource.GetDefaultView(Options);
        var needle = TextNormalizer.Normalize(FilterText);
        view.Filter = needle.Length == 0
            ? null
            : o => o is FilterOption f && TextNormalizer.Normalize(f.Label).Contains(needle, StringComparison.Ordinal);
    }

    private void UpdateText()
    {
        var options = Options?.OfType<FilterOption>().ToList() ?? [];
        HasOptions = options.Count > 0;
        HasSearch = options.Count > SearchThreshold;
        var selected = options.Count(o => o.IsSelected);
        var header = Loc.T(Header);
        ToggleText = selected switch
        {
            0 => header,
            1 => $"{header}: {options.First(o => o.IsSelected).Label}",
            _ => $"{header} · {selected}"
        };
    }
}

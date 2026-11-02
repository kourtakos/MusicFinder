using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MusicFinder.App.ViewModels;

public enum AppTab { Library, Tags, History, Session, Settings }

/// <summary>A tab whose feature arrives in a later milestone.</summary>
public sealed record PlaceholderViewModel(string Title, string Message);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly PlaceholderViewModel _history = new("History", "Play history and importers arrive in milestones M3–M4.");

    public LibraryViewModel Library { get; }
    public SettingsViewModel Settings { get; }
    public TagsViewModel Tags { get; }
    public SessionViewModel Session { get; }
    private readonly Services.SearchIndexCoordinator _searchIndex;

    /// <summary>"Preparing Greek search… 1,200 left" while the smart-search index is built in the background.</summary>
    public string IndexStatusText => _searchIndex.StatusText;
    public bool HasIndexStatus => _searchIndex.IsIndexing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentView))]
    private AppTab _selectedTab = AppTab.Library;

    public MainViewModel(LibraryViewModel library, SettingsViewModel settings, TagsViewModel tags, SessionViewModel session,
        Services.SearchIndexCoordinator searchIndex)
    {
        Library = library;
        Settings = settings;
        Tags = tags;
        Session = session;
        _searchIndex = searchIndex;
        _searchIndex.StatusChanged += (_, _) => { OnPropertyChanged(nameof(IndexStatusText)); OnPropertyChanged(nameof(HasIndexStatus)); };
    }

    public object CurrentView => SelectedTab switch
    {
        AppTab.Library => Library,
        AppTab.Tags => Tags,
        AppTab.History => _history,
        AppTab.Session => Session,
        _ => Settings
    };

    [RelayCommand]
    private void Navigate(AppTab tab) => SelectedTab = tab;

    partial void OnSelectedTabChanged(AppTab value)
    {
        if (value == AppTab.Tags) _ = Tags.ReloadAsync(); // always show fresh counts when the tab is opened
    }

    /// <summary>Ctrl+F / Ctrl+K: switch to the library and focus search.</summary>
    [RelayCommand]
    private void FocusSearch()
    {
        SelectedTab = AppTab.Library;
        Library.RequestFocusSearch();
    }

    public async Task InitializeAsync()
    {
        await Library.InitializeAsync();
        await Settings.ReloadAsync();
        await Tags.ReloadAsync();
        await Session.ReloadAsync();
    }
}

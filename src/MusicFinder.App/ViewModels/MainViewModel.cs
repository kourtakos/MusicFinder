using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MusicFinder.App.ViewModels;

public enum AppTab { Library, Tags, Health, History, Session, Settings }

/// <summary>A tab whose feature arrives in a later milestone.</summary>
public sealed record PlaceholderViewModel(string Title, string Message);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly PlaceholderViewModel _history = new("History", "Play history and importers arrive in milestones M3–M4.");

    public LibraryViewModel Library { get; }
    public SettingsViewModel Settings { get; }
    public TagsViewModel Tags { get; }
    public SessionViewModel Session { get; }
    public HealthViewModel Health { get; }
    private readonly Services.SearchIndexCoordinator _searchIndex;
    private readonly MusicFinder.Domain.Contracts.IFileOperationService _fileOperations;
    private readonly Services.IUserDialogs _dialogs;

    /// <summary>"Preparing Greek search… 1,200 left" while the smart-search index is built in the background.</summary>
    public string IndexStatusText => _searchIndex.StatusText;
    public bool HasIndexStatus => _searchIndex.IsIndexing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentView))]
    private AppTab _selectedTab = AppTab.Library;

    public MainViewModel(LibraryViewModel library, SettingsViewModel settings, TagsViewModel tags, SessionViewModel session,
        HealthViewModel health, Services.SearchIndexCoordinator searchIndex,
        MusicFinder.Domain.Contracts.IFileOperationService fileOperations, Services.IUserDialogs dialogs)
    {
        _fileOperations = fileOperations;
        _dialogs = dialogs;
        Library = library;
        Settings = settings;
        Tags = tags;
        Session = session;
        Health = health;
        Health.ShowInLibraryRequested += (_, query) => { SelectedTab = AppTab.Library; Library.ShowFiltered(query); };
        _searchIndex = searchIndex;
        _searchIndex.StatusChanged += (_, _) => { OnPropertyChanged(nameof(IndexStatusText)); OnPropertyChanged(nameof(HasIndexStatus)); };
    }

    public object CurrentView => SelectedTab switch
    {
        AppTab.Library => Library,
        AppTab.Tags => Tags,
        AppTab.Health => Health,
        AppTab.History => _history,
        AppTab.Session => Session,
        _ => Settings
    };

    [RelayCommand]
    private void Navigate(AppTab tab) => SelectedTab = tab;

    partial void OnSelectedTabChanged(AppTab value)
    {
        if (value == AppTab.Tags) _ = Tags.ReloadAsync(); // always show fresh counts when the tab is opened
        if (value == AppTab.Health) _ = Health.ReloadAsync();
    }

    /// <summary>Ctrl+F / Ctrl+K: switch to the library and focus search.</summary>
    [RelayCommand]
    private void FocusSearch()
    {
        SelectedTab = AppTab.Library;
        Library.RequestFocusSearch();
    }

    /// <summary>Settles a rename a crash left half-done, before the first scan could mistake it for a deleted file.</summary>
    private async Task RecoverFileOperationsAsync()
    {
        var result = await _fileOperations.RecoverAsync(CancellationToken.None);
        if (result.NeedAttention.Count > 0)
            _dialogs.ShowInfo("Files need a look", "A rename was interrupted and these files could not be settled automatically:\n"
                + string.Join("\n", result.NeedAttention.Take(10).Select(f => Path.GetFileName(f.Path) + ": " + f.Reason)));
    }

    public async Task InitializeAsync()
    {
        await RecoverFileOperationsAsync();
        await Library.InitializeAsync();
        await Settings.ReloadAsync();
        await Tags.ReloadAsync();
        await Session.ReloadAsync();
    }
}

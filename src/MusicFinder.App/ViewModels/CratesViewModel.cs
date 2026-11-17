using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Localization;
using MusicFinder.App.Services;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.App.ViewModels;

/// <summary>One crate in the left panel and in the "Add to crate" menu.</summary>
public sealed partial class CrateItemViewModel : ObservableObject
{
    public long Id { get; }
    public string Name { get; }
    [ObservableProperty] private int _trackCount;

    /// <summary>Adds the tracks selected in the grid to this crate.</summary>
    public IAsyncRelayCommand AddSelectedCommand { get; }
    public IAsyncRelayCommand OpenCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }

    public string Display => $"{Name}  ({TrackCount:N0})";
    partial void OnTrackCountChanged(int value) => OnPropertyChanged(nameof(Display));

    public CrateItemViewModel(long id, string name, int trackCount, Func<CrateItemViewModel, Task> add,
        Func<CrateItemViewModel, Task> open, Func<CrateItemViewModel, Task> delete)
    {
        Id = id;
        Name = name;
        _trackCount = trackCount;
        AddSelectedCommand = new AsyncRelayCommand(() => add(this));
        OpenCommand = new AsyncRelayCommand(() => open(this));
        DeleteCommand = new AsyncRelayCommand(() => delete(this));
    }
}

/// <summary>One saved search in the left panel. Opening it runs the search again.</summary>
public sealed class SavedSearchItemViewModel
{
    public long Id { get; }
    public string Name { get; }
    public IRelayCommand OpenCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }

    public SavedSearchItemViewModel(long id, string name, Action<SavedSearchItemViewModel> open, Func<SavedSearchItemViewModel, Task> delete)
    {
        Id = id;
        Name = name;
        OpenCommand = new RelayCommand(() => open(this));
        DeleteCommand = new AsyncRelayCommand(() => delete(this));
    }
}

/// <summary>The Crates list: named, ordered track lists for preparing a set. Crates only reference tracks.</summary>
public sealed partial class CratesViewModel : ObservableObject
{
    private readonly ICrateRepository _crates;
    private readonly SearchViewModel _search;
    private readonly IUserDialogs _dialogs;
    private readonly ISavedSearchRepository _saved;
    private readonly Dictionary<long, string> _savedJson = [];

    public ObservableCollection<CrateItemViewModel> Items { get; } = [];
    public ObservableCollection<SavedSearchItemViewModel> SavedSearches { get; } = [];

    /// <summary>The name typed in the "new crate" box.</summary>
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string? _message;

    public CratesViewModel(ICrateRepository crates, SearchViewModel search, IUserDialogs dialogs, ISavedSearchRepository saved)
    {
        _saved = saved;
        _crates = crates;
        _search = search;
        _dialogs = dialogs;
        _search.CratesChanged += (_, _) => _ = ReloadAsync();
    }

    // Reloads are queued one after another and each reads the data only once it holds the turn, so a slow earlier reload
    // can never put an out-of-date list on screen after a newer change.
    private readonly SemaphoreSlim _reloadTurn = new(1, 1);

    public async Task ReloadAsync()
    {
        await _reloadTurn.WaitAsync();
        try
        {
            var all = await _crates.GetAllAsync();
            var saved = await _saved.GetAllAsync();

            Items.Clear();
            foreach (var c in all)
                Items.Add(new CrateItemViewModel(c.Id, c.Name, c.TrackCount, AddAsync, OpenAsync, DeleteAsync));

            SavedSearches.Clear();
            _savedJson.Clear();
            foreach (var s in saved)
            {
                _savedJson[s.Id] = s.StateJson;
                SavedSearches.Add(new SavedSearchItemViewModel(s.Id, s.Name, OpenSaved, DeleteSavedAsync));
            }
        }
        finally { _reloadTurn.Release(); }
    }

    private void OpenSaved(SavedSearchItemViewModel item)
    {
        var state = MusicFinder.Application.Search.SavedSearchState.TryParse(_savedJson.GetValueOrDefault(item.Id));
        if (state is null)
        {
            Message = Loc.T("The saved search \"{0}\" could not be read.", item.Name);
            return;
        }
        Message = null;
        _search.ApplySearchState(state);
    }

    private async Task DeleteSavedAsync(SavedSearchItemViewModel item)
    {
        if (!_dialogs.Confirm(Loc.T("Delete saved search"),
                Loc.T("Delete the saved search \"{0}\"? No tracks are affected.", item.Name)))
            return;
        await _saved.DeleteAsync(item.Id);
        await ReloadAsync();
    }

    /// <summary>Keeps the current query, filters and sort under the name typed in the box.</summary>
    [RelayCommand]
    private async Task SaveSearch()
    {
        var name = NewName.Trim();
        if (name.Length == 0) { Message = Loc.T("Type a name for the search first."); return; }
        if (name.Length > 100) { Message = Loc.T("A name can be at most 100 characters."); return; }
        var state = _search.CaptureSearchState();
        if (state.IsEmpty) { Message = Loc.T("There is nothing to save: type a search or choose a filter first."); return; }
        var id = await _saved.AddAsync(name, state.ToJson());
        if (id is null) { Message = Loc.T("A saved search named \"{0}\" already exists.", name); return; }
        Message = null;
        NewName = "";
        await ReloadAsync();
    }

    private Task AddAsync(CrateItemViewModel crate) => _search.AddSelectedToCrateAsync(crate.Id, crate.Name);

    private Task OpenAsync(CrateItemViewModel crate) => _search.ShowCrateAsync(crate.Id, crate.Name);

    private async Task DeleteAsync(CrateItemViewModel crate)
    {
        if (!_dialogs.Confirm(Loc.T("Delete crate"),
                Loc.T("Delete the crate \"{0}\"? Only the list is removed; the tracks and files stay where they are.", crate.Name)))
            return;
        await _crates.DeleteAsync(crate.Id);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task Create()
    {
        var name = NewName.Trim();
        if (name.Length == 0) return;
        if (name.Length > 100) { Message = Loc.T("A crate name can be at most 100 characters."); return; }
        var id = await _crates.CreateAsync(name);
        if (id is null) { Message = Loc.T("A crate named \"{0}\" already exists.", name); return; }
        Message = null;
        NewName = "";
        await ReloadAsync();
        // A new crate is usually made to receive the selection straight away.
        var created = Items.FirstOrDefault(c => c.Id == id);
        if (created is not null) await _search.AddSelectedToCrateAsync(created.Id, created.Name);
    }
}

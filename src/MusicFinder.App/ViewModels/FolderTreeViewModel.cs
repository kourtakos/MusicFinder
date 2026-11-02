using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Services;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.App.ViewModels;

public partial class FolderTreeViewModel : ObservableObject
{
    private readonly ILibraryRootRepository _roots;
    private readonly IFolderRepository _folders;
    private readonly LibraryRootService _rootService;
    private readonly ScanCoordinator _scans;
    private readonly IUserDialogs _dialogs;
    private readonly DispatcherTimer _refreshTimer;
    private bool _refreshing;

    public ObservableCollection<FolderNodeViewModel> Roots { get; } = [];

    [ObservableProperty] private FolderNodeViewModel? _selectedNode;
    [ObservableProperty] private bool _includeSubfolders = true;
    [ObservableProperty] private bool _globalSearch;

    /// <summary>Raised when the selected folder or the scope toggles change.</summary>
    public event EventHandler? ScopeChanged;

    public FolderTreeViewModel(ILibraryRootRepository roots, IFolderRepository folders, LibraryRootService rootService,
        ScanCoordinator scans, IUserDialogs dialogs)
    {
        _roots = roots;
        _folders = folders;
        _rootService = rootService;
        _scans = scans;
        _dialogs = dialogs;

        _rootService.RootsChanged += async (_, _) => await ReloadAsync();
        _scans.ScanFinished += async (_, _) => await RefreshAsync();

        // While a scan runs, keep tree counts live without rebuilding the tree.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += async (_, _) => { if (_scans.IsScanning) await RefreshAsync(); };
        _refreshTimer.Start();
    }

    public long? SelectedFolderId => SelectedNode?.Id;
    public string ScopeLabel => GlobalSearch || SelectedNode is null ? "All" : SelectedNode.Name;

    partial void OnSelectedNodeChanged(FolderNodeViewModel? value) => RaiseScope();
    partial void OnIncludeSubfoldersChanged(bool value) => RaiseScope();
    partial void OnGlobalSearchChanged(bool value) => RaiseScope();

    private void RaiseScope()
    {
        OnPropertyChanged(nameof(ScopeLabel));
        ScopeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Choosing a different folder means the user wants that folder, so a leftover "Global search" (for example from opening a Library Health finding) is switched off.</summary>
    internal void NodeSelected(FolderNodeViewModel node)
    {
        if (GlobalSearch && node.Id != SelectedNode?.Id) GlobalSearch = false;
        SelectedNode = node;
    }

    public async Task ReloadAsync()
    {
        var selectedId = SelectedNode?.Id;
        var roots = (await Task.Run(() => _roots.GetAllAsync())).Where(r => r.Enabled).ToList();

        Roots.Clear();
        foreach (var root in roots)
        {
            var tops = await Task.Run(() => _folders.GetChildrenAsync(root.Id, null));
            foreach (var top in tops)
            {
                var node = new FolderNodeViewModel(top, isRoot: true, _folders, NodeSelected);
                Roots.Add(node);
                if (roots.Count == 1) node.IsExpanded = true;
            }
        }

        if (selectedId is not null && Roots.All(r => r.Id != selectedId))
            SelectedNode = null;
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var enabled = (await Task.Run(() => _roots.GetAllAsync())).Count(r => r.Enabled);
            if (enabled != Roots.Select(r => r.RootId).Distinct().Count())
            {
                await ReloadAsync(); // a root's first folder appeared (or one was removed)
                return;
            }
            foreach (var root in Roots.ToList()) await root.RefreshAsync();
        }
        finally { _refreshing = false; }
    }

    [RelayCommand]
    private async Task AddRootAsync()
    {
        var path = _dialogs.PickFolder();
        if (path is null) return;
        var result = await _rootService.AddAsync(path);
        if (!result.Success) _dialogs.ShowError("Cannot add folder", result.Message!);
    }

    /// <summary>Rescans the root of the given node (or of the selected node; all roots when nothing is selected).</summary>
    [RelayCommand]
    private void RescanNode(FolderNodeViewModel? node)
    {
        node ??= SelectedNode;
        if (node is not null) _rootService.Rescan(node.RootId);
        else _ = _rootService.RescanAllAsync();
    }

    [RelayCommand]
    private void OpenNodeInExplorer(FolderNodeViewModel? node)
    {
        node ??= SelectedNode;
        if (node is not null) _dialogs.RevealInExplorer(node.FullPath);
    }

    private static bool CanExclude(FolderNodeViewModel? node) => node is { IsRoot: true };

    [RelayCommand(CanExecute = nameof(CanExclude))]
    private async Task ExcludeRootAsync(FolderNodeViewModel? node)
    {
        if (node is not { IsRoot: true }) return;
        if (!_dialogs.Confirm("Exclude folder",
                $"Stop indexing '{node.FullPath}'?\n\nYour music files are not touched. You can re-enable it in Settings."))
            return;
        await _rootService.SetEnabledAsync(node.RootId, false);
    }
}

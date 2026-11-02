using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;

namespace MusicFinder.App.ViewModels;

/// <summary>A folder in the tree. Children load lazily on first expand; a placeholder child shows the expander.</summary>
public partial class FolderNodeViewModel : ObservableObject
{
    private readonly IFolderRepository _folders;
    private readonly Action<FolderNodeViewModel> _onSelected;
    private bool _loaded;

    public long Id { get; private set; }
    public long RootId { get; }
    public string FullPath { get; private set; } = "";
    public bool IsRoot { get; }
    public bool IsPlaceholder { get; }
    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _trackCount;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public string CountText => IsPlaceholder ? "" : $"({TrackCount:N0})";

    private FolderNodeViewModel(IFolderRepository folders, Action<FolderNodeViewModel> onSelected, bool placeholder)
    {
        _folders = folders;
        _onSelected = onSelected;
        IsPlaceholder = placeholder;
    }

    public FolderNodeViewModel(Folder folder, bool isRoot, IFolderRepository folders, Action<FolderNodeViewModel> onSelected)
        : this(folders, onSelected, placeholder: false)
    {
        Id = folder.Id;
        RootId = folder.RootId;
        IsRoot = isRoot;
        Apply(folder);
        Children.Add(new FolderNodeViewModel(folders, onSelected, placeholder: true));
    }

    private void Apply(Folder folder)
    {
        FullPath = folder.FullPath;
        Name = IsRoot ? folder.FullPath : folder.Name;
        TrackCount = folder.RecursiveTrackCount;
    }

    partial void OnTrackCountChanged(int value) => OnPropertyChanged(nameof(CountText));

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !IsPlaceholder) _onSelected(this);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded) _ = LoadChildrenAsync();
    }

    private async Task LoadChildrenAsync()
    {
        _loaded = true;
        var children = await Task.Run(() => _folders.GetChildrenAsync(RootId, Id));
        Children.Clear();
        foreach (var child in children)
            Children.Add(new FolderNodeViewModel(child, false, _folders, _onSelected));
    }

    /// <summary>Updates counts and merges new/removed children without disturbing expansion or selection.</summary>
    public async Task RefreshAsync()
    {
        var fresh = await Task.Run(() => _folders.GetAsync(Id));
        if (fresh is not null) Apply(fresh);
        if (!_loaded) return;

        var current = await Task.Run(() => _folders.GetChildrenAsync(RootId, Id));
        var byId = Children.Where(c => !c.IsPlaceholder).ToDictionary(c => c.Id);
        var wanted = current.Select(c => c.Id).ToHashSet();

        foreach (var gone in Children.Where(c => c.IsPlaceholder || !wanted.Contains(c.Id)).ToList())
            Children.Remove(gone);

        var index = 0;
        foreach (var folder in current)
        {
            if (byId.TryGetValue(folder.Id, out var node))
            {
                await node.RefreshAsync();
            }
            else
            {
                node = new FolderNodeViewModel(folder, false, _folders, _onSelected);
                Children.Insert(Math.Min(index, Children.Count), node);
            }
            index++;
        }
    }
}

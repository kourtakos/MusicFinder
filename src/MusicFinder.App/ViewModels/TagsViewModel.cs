using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Localization;
using MusicFinder.App.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.ViewModels;

/// <summary>A DJ tag row. Editing the name and leaving the box (or pressing Enter) renames it.</summary>
public sealed partial class TagItemViewModel : ObservableObject
{
    private readonly Func<TagItemViewModel, string, Task> _rename;
    private bool _silent;

    public long Id { get; }
    public int TrackCount { get; }
    public string CountText => TrackCount == 1 ? Loc.T("1 track") : Loc.T("{0:N0} tracks", TrackCount);
    public string OriginalName { get; private set; }

    [ObservableProperty] private string _name;

    public TagItemViewModel(DjTagInfo info, Func<TagItemViewModel, string, Task> rename)
    {
        Id = info.Tag.Id;
        TrackCount = info.TrackCount;
        OriginalName = info.Tag.Name;
        _name = info.Tag.Name;
        _rename = rename;
    }

    async partial void OnNameChanged(string value)
    {
        if (_silent || value == OriginalName) return;
        await _rename(this, value);
    }

    /// <summary>Put the box back to what is stored (after a rejected rename), without triggering another rename.</summary>
    public void Revert()
    {
        _silent = true;
        try { Name = OriginalName; }
        finally { _silent = false; }
    }

    public void Commit(string name) => OriginalName = name;
}

/// <summary>A node of the genre tree shown in the Tags tab.</summary>
public sealed partial class GenreNodeItem : ObservableObject
{
    private readonly Action<GenreNodeItem> _onSelected;

    public long Id { get; }
    public string Name { get; }
    public long? ParentId { get; }
    public bool IsBuiltIn { get; }
    public int TrackCount { get; }
    public string CountText => TrackCount > 0 ? $"({TrackCount:N0})" : "";
    public ObservableCollection<GenreNodeItem> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public GenreNodeItem(GenreNode node, Action<GenreNodeItem> onSelected)
    {
        Id = node.Genre.Id;
        Name = node.Genre.Name;
        ParentId = node.Genre.ParentId;
        IsBuiltIn = node.Genre.IsBuiltIn;
        TrackCount = node.TrackCount;
        _onSelected = onSelected;
        foreach (var child in node.Children) Children.Add(new GenreNodeItem(child, onSelected));
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _onSelected(this);
    }

    public IEnumerable<GenreNodeItem> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants()) yield return d;
    }
}

public sealed record AliasItem(string Alias, string GenreName);

/// <summary>One choice in the "move under" box; Id null means top level.</summary>
public sealed record ParentChoice(long? Id, string Label, int Depth)
{
    public string Display => Depth == 0 ? Label : new string(' ', Depth * 3) + Label;
}

/// <summary>
/// The Tags tab: edit the genre taxonomy and its aliases, manage DJ tags, and map genre values found in files that
/// match nothing yet. Nothing here touches the music files; it only changes how MusicFinder groups them.
/// </summary>
public sealed partial class TagsViewModel : ObservableObject
{
    private readonly IGenreService _genres;
    private readonly IDjTagService _tags;
    private readonly IUserDialogs _dialogs;
    private readonly SynchronizationContext _ui;
    private bool _loading;

    public ObservableCollection<TagItemViewModel> DjTags { get; } = [];
    public ObservableCollection<GenreNodeItem> Taxonomy { get; } = [];
    public ObservableCollection<UnmappedGenre> Unmapped { get; } = [];
    public ObservableCollection<AliasItem> Aliases { get; } = [];
    public ObservableCollection<ParentChoice> ParentChoices { get; } = [];

    [ObservableProperty] private GenreNodeItem? _selectedGenre;
    [ObservableProperty] private UnmappedGenre? _selectedUnmapped;
    [ObservableProperty] private ParentChoice? _selectedParent;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _newAlias = "";
    [ObservableProperty] private string _newSubGenre = "";
    [ObservableProperty] private string _newTopGenre = "";
    [ObservableProperty] private string _newTagName = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _messageIsError;

    public bool HasSelection => SelectedGenre is not null;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasUnmapped => Unmapped.Count > 0;
    public string SelectedGenreTitle => SelectedGenre is null ? "" : SelectedGenre.Name;
    public string SelectedGenreSubtitle => SelectedGenre switch
    {
        null => "",
        { IsBuiltIn: true } => Loc.T("Built-in genre · you can still rename, move or remove it"),
        _ => Loc.T("Your own genre")
    };
    public bool CanMap => SelectedGenre is not null && SelectedUnmapped is not null;
    public bool CanCreateFromUnmapped => SelectedUnmapped is not null;

    public TagsViewModel(IGenreService genres, IDjTagService tags, IUserDialogs dialogs)
    {
        _genres = genres;
        _tags = tags;
        _dialogs = dialogs;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _genres.TaxonomyChanged += (_, _) => OnUi(ReloadAsync);
        _tags.TagsChanged += (_, _) =>
        {
            // Our own rename already updated its row in place; rebuilding the list would replace the box the user is typing in.
            if (!_renamingTag) OnUi(ReloadAsync);
        };
    }

    private bool _renamingTag;

    private void OnUi(Func<Task> work)
    {
        if (SynchronizationContext.Current == _ui) _ = work();
        else _ui.Post(_ => _ = work(), null);
    }

    // ===== loading =====

    public async Task ReloadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var selectedId = SelectedGenre?.Id;
            var selectedUnmapped = SelectedUnmapped?.Value;
            var expanded = Taxonomy.SelectMany(n => n.SelfAndDescendants()).Where(n => n.IsExpanded).Select(n => n.Id).ToHashSet();

            var tree = await _genres.GetTaxonomyAsync();
            var tags = await _tags.GetAllAsync();
            var unmapped = await _genres.GetUnmappedAsync();

            DjTags.Clear();
            foreach (var t in tags) DjTags.Add(new TagItemViewModel(t, RenameTagAsync));

            Taxonomy.Clear();
            foreach (var root in tree) Taxonomy.Add(new GenreNodeItem(root, g => SelectedGenre = g));
            foreach (var node in Taxonomy.SelectMany(n => n.SelfAndDescendants()))
                if (expanded.Contains(node.Id)) node.IsExpanded = true;

            Unmapped.Clear();
            foreach (var u in unmapped) Unmapped.Add(u);
            OnPropertyChanged(nameof(HasUnmapped));
            SelectedUnmapped = selectedUnmapped is null ? null : Unmapped.FirstOrDefault(u => u.Value == selectedUnmapped);

            var again = selectedId is null ? null : Taxonomy.SelectMany(n => n.SelfAndDescendants()).FirstOrDefault(n => n.Id == selectedId);
            SelectedGenre = null; // force the editor to rebuild from the fresh node
            if (again is not null) again.IsSelected = true;
            else await LoadSelectionAsync();
        }
        catch (Exception ex)
        {
            Say(Loc.T("Could not load: {0}", ex.Message), error: true);
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnSelectedGenreChanged(GenreNodeItem? value) => _ = LoadSelectionAsync();
    partial void OnSelectedUnmappedChanged(UnmappedGenre? value)
    {
        OnPropertyChanged(nameof(CanMap));
        OnPropertyChanged(nameof(CanCreateFromUnmapped));
    }

    private async Task LoadSelectionAsync()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedGenreTitle));
        OnPropertyChanged(nameof(SelectedGenreSubtitle));
        OnPropertyChanged(nameof(CanMap));

        var genre = SelectedGenre;
        Aliases.Clear();
        ParentChoices.Clear();
        EditName = genre?.Name ?? "";
        NewAlias = NewSubGenre = "";
        if (genre is null) { SelectedParent = null; return; }

        // Where it can move: anywhere except itself and its own sub-genres (that would make a loop).
        var blocked = genre.SelfAndDescendants().Select(n => n.Id).ToHashSet();
        ParentChoices.Add(new ParentChoice(null, Loc.T("(top level)"), 0));
        void Walk(GenreNodeItem n, int depth)
        {
            if (blocked.Contains(n.Id)) return;
            ParentChoices.Add(new ParentChoice(n.Id, n.Name, depth + 1));
            foreach (var c in n.Children) Walk(c, depth + 1);
        }
        foreach (var root in Taxonomy) Walk(root, 0);
        SelectedParent = ParentChoices.FirstOrDefault(p => p.Id == genre.ParentId) ?? ParentChoices[0];

        var aliases = await _genres.GetAliasesAsync();
        if (!ReferenceEquals(genre, SelectedGenre)) return; // selection moved while loading
        foreach (var a in aliases.Where(a => a.GenreName == genre.Name).OrderBy(a => a.Alias, StringComparer.CurrentCultureIgnoreCase))
            Aliases.Add(new AliasItem(a.Alias, a.GenreName));
    }

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    // ===== helpers =====

    private void Say(string text, bool error = false)
    {
        MessageIsError = error;
        Message = text;
    }

    /// <summary>Runs an edit, turning validation failures into a visible message instead of an exception.</summary>
    private async Task<bool> TryAsync(Func<Task> action, string? success = null)
    {
        try
        {
            await action();
            if (success is not null) Say(success);
            else Message = null;
            return true;
        }
        catch (TaxonomyException ex)
        {
            Say(ex.Message, error: true);
            return false;
        }
        catch (Exception ex)
        {
            Say(Loc.T("That did not work: {0}", ex.Message), error: true);
            return false;
        }
    }

    // ===== DJ tags =====

    [RelayCommand]
    private async Task AddTagAsync()
    {
        var name = NewTagName;
        if (await TryAsync(() => _tags.AddAsync(name), Loc.T("Added the tag '{0}'.", name.Trim()))) NewTagName = "";
    }

    private async Task RenameTagAsync(TagItemViewModel item, string name)
    {
        _renamingTag = true;
        bool ok;
        try { ok = await TryAsync(() => _tags.RenameAsync(item.Id, name), Loc.T("Renamed to '{0}'.", name.Trim())); }
        finally { _renamingTag = false; }

        if (ok) item.Commit(name.Trim());
        else item.Revert(); // keep the box honest: it shows what is actually stored
    }

    [RelayCommand]
    private async Task DeleteTagAsync(TagItemViewModel? item)
    {
        if (item is null) return;
        var used = item.TrackCount > 0 ? "\n\n" + Loc.T("It is on {0}; the tag is removed from them. The tracks and files are not changed.", item.CountText) : "";
        if (!_dialogs.Confirm(Loc.T("Delete tag"), Loc.T("Delete the tag '{0}'?", item.OriginalName) + used)) return;
        await TryAsync(() => _tags.DeleteAsync(item.Id), Loc.T("Deleted the tag '{0}'.", item.OriginalName));
    }

    // ===== genres =====

    [RelayCommand]
    private async Task AddTopGenreAsync()
    {
        var name = NewTopGenre;
        if (await TryAsync(() => _genres.AddGenreAsync(name, null), Loc.T("Added the genre '{0}'.", name.Trim()))) NewTopGenre = "";
    }

    [RelayCommand]
    private async Task AddSubGenreAsync()
    {
        if (SelectedGenre is not { } parent) return;
        var name = NewSubGenre;
        if (await TryAsync(() => _genres.AddGenreAsync(name, parent.Id), Loc.T("Added '{0}' under {1}.", name.Trim(), parent.Name)))
        {
            NewSubGenre = "";
            parent.IsExpanded = true;
        }
    }

    [RelayCommand]
    private async Task RenameGenreAsync()
    {
        if (SelectedGenre is not { } genre) return;
        var name = EditName;
        await TryAsync(() => _genres.RenameGenreAsync(genre.Id, name),
            Loc.T("Renamed to '{0}'. Files tagged with the old name still map to it.", name.Trim()));
    }

    [RelayCommand]
    private async Task MoveGenreAsync()
    {
        if (SelectedGenre is not { } genre || SelectedParent is not { } target) return;
        await TryAsync(() => _genres.MoveGenreAsync(genre.Id, target.Id),
            target.Id is null ? Loc.T("Moved '{0}' to the top level.", genre.Name) : Loc.T("Moved '{0}' under {1}.", genre.Name, target.Label));
    }

    [RelayCommand]
    private async Task DeleteGenreAsync()
    {
        if (SelectedGenre is not { } genre) return;
        if (genre.Children.Count > 0)
        {
            Say(Loc.T("'{0}' still has sub-genres. Move or delete them first.", genre.Name), error: true);
            return;
        }
        var used = genre.TrackCount == 1
            ? "\n\n" + Loc.T("1 track is in it; it stays in your library and keeps its embedded genre text, but loses this grouping.")
            : genre.TrackCount > 1
                ? "\n\n" + Loc.T("{0:N0} tracks are in it; they stay in your library and keep their embedded genre text, but lose this grouping.", genre.TrackCount)
                : "";
        if (!_dialogs.Confirm(Loc.T("Delete genre"), Loc.T("Delete the genre '{0}' and its aliases?", genre.Name) + used)) return;

        var name = genre.Name;
        SelectedGenre = null;
        await TryAsync(() => _genres.DeleteGenreAsync(genre.Id), Loc.T("Deleted the genre '{0}'.", name));
    }

    // ===== aliases =====

    [RelayCommand]
    private async Task AddAliasAsync()
    {
        if (SelectedGenre is not { } genre) return;
        var alias = NewAlias;
        if (await TryAsync(() => _genres.AddAliasAsync(alias, genre.Id), Loc.T("'{0}' now maps to {1}.", alias.Trim(), genre.Name))) NewAlias = "";
    }

    [RelayCommand]
    private async Task RemoveAliasAsync(AliasItem? alias)
    {
        if (alias is null) return;
        await TryAsync(() => _genres.RemoveAliasAsync(alias.Alias), Loc.T("Removed the alias '{0}'.", alias.Alias));
    }

    // ===== values found in files that match nothing =====

    [RelayCommand]
    private async Task MapUnmappedAsync()
    {
        if (SelectedUnmapped is not { } value || SelectedGenre is not { } genre) return;
        await TryAsync(() => _genres.AddAliasAsync(value.Value, genre.Id),
            value.TrackCount == 1
                ? Loc.T("'{0}' now maps to {1} (1 track updated).", value.Value, genre.Name)
                : Loc.T("'{0}' now maps to {1} ({2:N0} tracks updated).", value.Value, genre.Name, value.TrackCount));
    }

    [RelayCommand]
    private async Task CreateGenreFromUnmappedAsync()
    {
        if (SelectedUnmapped is not { } value) return;
        var parent = SelectedGenre?.Id; // creates it under the selected genre when one is selected, else top level
        await TryAsync(() => _genres.AddGenreAsync(value.Value, parent),
            parent is null ? Loc.T("Created the genre '{0}'.", value.Value) : Loc.T("Created '{0}' under {1}.", value.Value, SelectedGenre!.Name));
    }
}

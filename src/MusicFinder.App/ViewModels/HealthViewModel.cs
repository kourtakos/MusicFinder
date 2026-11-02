using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Services;
using MusicFinder.Application.Health;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.ViewModels;

/// <summary>One finding on the Library Health tab: what it is, how many, and the button that lists them in the Library.</summary>
public sealed partial class HealthCardViewModel : ObservableObject
{
    private readonly Action<HealthCardViewModel> _show;

    public HealthIssueKind Kind { get; }
    public string Title { get; }
    public string Explanation { get; }
    /// <summary>The search that lists these tracks in the Library; null when they cannot be listed there.</summary>
    public string? Query { get; }
    public bool CanShow => Query is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(HasFindings), nameof(CanShowNow))]
    private int _count;

    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
    public bool HasFindings => Count > 0;
    public bool CanShowNow => CanShow && Count > 0;

    public HealthCardViewModel(HealthIssueKind kind, Action<HealthCardViewModel> show)
    {
        Kind = kind;
        Title = HealthIssues.Title(kind);
        Explanation = HealthIssues.Explain(kind);
        Query = HealthIssues.SearchFor(kind);
        _show = show;
    }

    [RelayCommand(CanExecute = nameof(CanShowNow))]
    private void Show() => _show(this);
}

public sealed record HealthGroupViewModel(string Title, string Subtitle, IReadOnlyList<HealthCardViewModel> Cards);

/// <summary>
/// Library Health: counts of what is missing or suspect, from the stored library data (no file is read when the tab opens).
/// A card opens the Library already filtered to exactly those tracks, where the usual actions (edit tags, identify,
/// analyze, open in Explorer) apply to a selection.
/// </summary>
public sealed partial class HealthViewModel : ObservableObject
{
    private readonly IMetadataHealthService _health;
    private readonly IDuplicateScanService _duplicates;
    private readonly IProbableDuplicateService _probable;
    private readonly IUserDialogs _dialogs;

    public IReadOnlyList<HealthGroupViewModel> Groups { get; }

    [ObservableProperty] private string _summaryText = "Counting…";
    [ObservableProperty] private string _unsupportedText = "";
    [ObservableProperty] private bool _hasUnsupported;
    [ObservableProperty] private bool _isLoading;

    /// <summary>Raised with the Library search that lists a card's tracks; the shell switches to the Library.</summary>
    public event EventHandler<string>? ShowInLibraryRequested;

    public HealthViewModel(IMetadataHealthService health, IDuplicateScanService duplicates, IUserDialogs dialogs, IProbableDuplicateService probable)
    {
        _probable = probable;
        _health = health;
        _duplicates = duplicates;
        _dialogs = dialogs;
        HealthCardViewModel Card(HealthIssueKind k) => new(k, OnShow);
        Groups =
        [
            new("Files", "What the library knows about the files themselves.",
                [Card(HealthIssueKind.MissingFiles), Card(HealthIssueKind.NotChecked)]),
            new("Tags", "Fields that are empty in the files. Fix them in the tag editor, or let Identify and Analyze fill them in.",
                [Card(HealthIssueKind.MissingTitle), Card(HealthIssueKind.MissingArtist), Card(HealthIssueKind.MissingAlbum),
                 Card(HealthIssueKind.MissingGenre), Card(HealthIssueKind.MissingYear), Card(HealthIssueKind.MissingBpm),
                 Card(HealthIssueKind.MissingKey), Card(HealthIssueKind.MissingArtwork), Card(HealthIssueKind.Mojibake)]),
            new("Quality", "Hints from the file's own properties. They are suspicions to look at, never proof, and nothing is changed because of them.",
                [Card(HealthIssueKind.Unreadable), Card(HealthIssueKind.LowBitrate), Card(HealthIssueKind.OddSampleRate),
                 Card(HealthIssueKind.CodecMismatch), Card(HealthIssueKind.SizeMismatch), Card(HealthIssueKind.ZeroDuration)]),
            new("Duplicates", "Files with identical content. Found by a scan you start yourself; the scan only reads files.",
                [Card(HealthIssueKind.ExactDuplicates)])
        ];
    }

    [RelayCommand]
    private async Task FindDuplicatesAsync()
    {
        var vm = new DuplicatesViewModel(_duplicates, _dialogs, query => ShowInLibraryRequested?.Invoke(this, query));
        _dialogs.ShowDuplicates(vm);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task FindProbableDuplicatesAsync()
    {
        var vm = new ProbableDuplicatesViewModel(_probable, _dialogs, query => ShowInLibraryRequested?.Invoke(this, query));
        _dialogs.ShowProbableDuplicates(vm);
        await Task.CompletedTask;
    }

    private void OnShow(HealthCardViewModel card)
    {
        if (card.Query is { } q) ShowInLibraryRequested?.Invoke(this, q);
    }

    public async Task ReloadAsync()
    {
        IsLoading = true;
        try
        {
            var summary = await _health.GetSummaryAsync(CancellationToken.None);
            foreach (var card in Groups.SelectMany(g => g.Cards))
            {
                card.Count = summary.CountOf(card.Kind);
                card.ShowCommand.NotifyCanExecuteChanged();
            }
            SummaryText = summary.TotalTracks == 0
                ? "The library is empty. Add a music folder in the Library tab."
                : $"{summary.TotalTracks:N0} tracks. Counts come from what the library already knows; nothing is re-read from disk.";
            HasUnsupported = summary.UnsupportedTotal > 0;
            UnsupportedText = HasUnsupported
                ? $"{summary.UnsupportedTotal:N0} audio-like files are not in the library because their format is not supported: " +
                  string.Join(", ", summary.Unsupported.Select(u => $"{u.Count:N0} × {u.Extension}")) + "."
                : "";
        }
        catch (Exception ex)
        {
            SummaryText = "Library Health could not be counted: " + ex.Message;
        }
        finally { IsLoading = false; }
    }
}

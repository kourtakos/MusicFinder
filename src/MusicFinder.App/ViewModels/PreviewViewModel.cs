using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.Application.Preview;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.ViewModels;

/// <summary>One choice in the output list. A null <see cref="Id"/> is the Windows default.</summary>
public sealed record PreviewDeviceOption(string? Id, string Label);

/// <summary>
/// The discreet player at the top of the window, and its Settings section. A preview is only a listen: it never touches the DJ session,
/// the play history or the files (they are opened read-only and shared).
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject, IDisposable
{
    public const string DefaultDeviceLabel = "Windows default output";

    private readonly IPreviewPlayer _player;
    private readonly IPreviewOutputDevices _devices;
    private readonly ISettingsStore _store;
    private readonly SynchronizationContext _ui;
    private CancellationTokenSource? _saveVolume;
    private bool _loading;
    private bool _dismissed;

    public PreviewViewModel(IPreviewPlayer player, IPreviewOutputDevices devices, ISettingsStore store, Services.Artwork.IArtworkService artwork)
    {
        _player = player;
        _devices = devices;
        _store = store;
        Artwork = artwork;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _loading = true;
        var settings = store.Load();
        _volume = Math.Clamp(settings.PreviewVolume, 0, 1);
        _savedDeviceId = string.IsNullOrWhiteSpace(settings.PreviewDeviceId) ? null : settings.PreviewDeviceId;
        _selectedDevice = new PreviewDeviceOption(_savedDeviceId, _savedDeviceId is null ? DefaultDeviceLabel : "Selected output");
        Devices.Add(_selectedDevice);
        _loading = false;

        _player.Volume = (float)_volume;
        _player.DeviceId = _savedDeviceId;
        _player.StateChanged += (_, status) => OnUi(() => Apply(status));
        _player.PositionChanged += (_, position) => OnUi(() => ApplyPosition(position));
        UpdateDeviceText();
    }

    public Services.Artwork.IArtworkService Artwork { get; }

    /// <summary>The output list for Settings: the Windows default first, then every active output.</summary>
    public ObservableCollection<PreviewDeviceOption> Devices { get; } = [];

    private string? _savedDeviceId;

    [ObservableProperty] private PreviewDeviceOption _selectedDevice;
    [ObservableProperty] private double _volume;
    [ObservableProperty] private PreviewState _state = PreviewState.Idle;
    [ObservableProperty] private string _timeText = PreviewTime.Format(TimeSpan.Zero, null);
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private string _audioPath = "";
    [ObservableProperty] private string? _artKey;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _deviceText = "";
    [ObservableProperty] private bool _deviceIsDefault = true;
    [ObservableProperty] private string _devicesStatus = "";

    /// <summary>The bar shows once something was previewed, until the user closes it.</summary>
    public bool HasTrack => State != PreviewState.Idle && !_dismissed;
    public bool IsPlaying => State == PreviewState.Playing;
    public bool IsLoading => State == PreviewState.Loading;
    public bool HasError => State == PreviewState.Failed && Error.Length > 0;
    public bool CanSeek => State is PreviewState.Playing or PreviewState.Paused;

    /// <summary>Segoe MDL2 glyph for the first button: pause while playing, play otherwise.</summary>
    public string PlayGlyph => IsPlaying ? "" : "";
    public string PlayToolTip => IsPlaying ? "Pause" : "Play";

    /// <summary>The caption scrolls only while playing, so a paused or finished preview costs nothing.</summary>
    public bool IsScrolling => IsPlaying;

    public string PathText => AudioPath;

    partial void OnStateChanged(PreviewState value)
    {
        OnPropertyChanged(nameof(HasTrack));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanSeek));
        OnPropertyChanged(nameof(PlayGlyph));
        OnPropertyChanged(nameof(PlayToolTip));
        OnPropertyChanged(nameof(IsScrolling));
    }

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnVolumeChanged(double value)
    {
        var v = Math.Clamp(value, 0, 1);
        _player.Volume = (float)v;
        if (!_loading) ScheduleSaveVolume(v);
    }

    partial void OnSelectedDeviceChanged(PreviewDeviceOption value)
    {
        if (_loading || value is null) return;
        _savedDeviceId = value.Id;
        _player.DeviceId = value.Id;
        Save(s => s.PreviewDeviceId = value.Id);
        UpdateDeviceText();
    }

    // ===== Commands =====

    /// <summary>Double-click on a row. The latest request wins.</summary>
    public void PlayRow(TrackRowViewModel? row)
    {
        if (row is null) return;
        _dismissed = false;
        PlayTrack(ToTrack(row.Row));
    }

    public static PreviewTrack ToTrack(SearchResultRow row) =>
        new(row.Id, row.FullPath, row.Title, row.Artist, row.Album, row.Year, row.GenreRaw is { Length: > 0 } ? string.Join(", ", row.Genres) : null,
            row.Bpm, row.MusicalKey, row.DurationMs, row.BitrateKbps, row.Extension, row.ArtworkCacheKey);

    private void PlayTrack(PreviewTrack track)
    {
        _ = RefreshDeviceNameAsync();                                  // enumerates outputs only when a preview is first used
        _ = _player.PlayAsync(track, default);
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        switch (_player.Status.State)
        {
            case PreviewState.Playing: _player.Pause(); break;
            case PreviewState.Paused: _player.Resume(); break;
            case PreviewState.Ended or PreviewState.Failed when _player.Status.Track is { } track: PlayTrack(track); break;
        }
    }

    /// <summary>Space in the grid: pauses or resumes what is loaded; it never starts sound by itself.</summary>
    public bool TogglePauseIfLoaded()
    {
        if (_player.Status.State is not (PreviewState.Playing or PreviewState.Paused)) return false;
        TogglePlayPause();
        return true;
    }

    [RelayCommand]
    private void Stop() => _player.Stop();

    [RelayCommand]
    private void Dismiss()
    {
        _player.Stop();
        _dismissed = true;
        OnPropertyChanged(nameof(HasTrack));
    }

    /// <summary>A click on the thin bar under the strip: 0 is the start, 1 the end.</summary>
    public void SeekToFraction(double fraction)
    {
        var status = _player.Status;                                   // the player is the truth; the view model's copy may lag a moment
        if (status.State is not (PreviewState.Playing or PreviewState.Paused) || status.Duration is not { } total) return;
        _player.Seek(TimeSpan.FromSeconds(total.TotalSeconds * Math.Clamp(fraction, 0, 1)));
    }

    /// <summary>Called before MusicFinder itself writes tags to or renames these files.</summary>
    public void ReleaseIfPlaying(IEnumerable<string> paths)
    {
        foreach (var path in paths) _player.ReleaseIfPlaying(path);
    }

    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        var list = await _devices.ListAsync(default);
        var chosen = _savedDeviceId;
        _loading = true;
        Devices.Clear();
        Devices.Add(new PreviewDeviceOption(null, DefaultDeviceLabel));
        foreach (var d in list) Devices.Add(new PreviewDeviceOption(d.Id, d.IsDefault ? d.Name + " (Windows default)" : d.Name));
        var match = Devices.FirstOrDefault(d => d.Id == chosen);
        if (match is null && chosen is not null)
        {
            match = new PreviewDeviceOption(chosen, "The chosen output is not connected");
            Devices.Add(match);
        }
        SelectedDevice = match ?? Devices[0];
        _loading = false;
        DevicesStatus = list.Count == 0 ? "No audio outputs were found." : "";
        UpdateDeviceText();
    }

    // ===== Updates from the player =====

    private async Task RefreshDeviceNameAsync()
    {
        try
        {
            if (_savedDeviceId is null) { UpdateDeviceText(); return; }
            var list = await _devices.ListAsync(default);
            var name = list.FirstOrDefault(d => d.Id == _savedDeviceId)?.Name;
            DeviceText = name is null ? "Chosen output not connected" : name;
            DeviceIsDefault = false;
        }
        catch (Exception) { /* the bar just keeps its previous text */ }
    }

    private void UpdateDeviceText()
    {
        DeviceIsDefault = _savedDeviceId is null;
        DeviceText = _savedDeviceId is null
            ? DefaultDeviceLabel
            : SelectedDevice.Id == _savedDeviceId && SelectedDevice.Label != "Selected output" ? SelectedDevice.Label : "Chosen output";
    }

    private void Apply(PreviewStatus status)
    {
        if (status.Track is { } track)
        {
            Title = track.Title;
            Caption = PreviewCaption.Build(track);
            AudioPath = track.FullPath;
            ArtKey = track.ArtKey;
        }
        Error = status.Error ?? "";
        State = status.State;
        TimeText = PreviewTime.Format(status.Position, status.Duration);
        Progress = Fraction(status.Position, status.Duration);
    }

    private void ApplyPosition(TimeSpan position)
    {
        var status = _player.Status;
        TimeText = PreviewTime.Format(position, status.Duration);
        Progress = Fraction(position, status.Duration);
    }

    private static double Fraction(TimeSpan position, TimeSpan? total) =>
        total is { } t && t > TimeSpan.Zero ? Math.Clamp(position.TotalSeconds / t.TotalSeconds, 0, 1) : 0;

    private void OnUi(Action work)
    {
        if (SynchronizationContext.Current == _ui) work();
        else _ui.Post(_ => work(), null);
    }

    private void ScheduleSaveVolume(double value)
    {
        _saveVolume?.Cancel();
        var cts = _saveVolume = new CancellationTokenSource();
        _ = Task.Delay(400, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) OnUi(() => Save(s => s.PreviewVolume = value));
        }, TaskScheduler.Default);
    }

    private void Save(Action<AppSettings> change)
    {
        var s = _store.Load();
        change(s);
        _store.Save(s);
    }

    public void Dispose()
    {
        _saveVolume?.Cancel();
        _player.Dispose();
    }
}

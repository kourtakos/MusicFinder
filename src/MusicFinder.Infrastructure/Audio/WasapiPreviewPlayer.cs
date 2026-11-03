using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MusicFinder.Application.Preview;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

// NAudio 3 marks WasapiOut obsolete in favour of WasapiPlayer. It still works; moving to WasapiPlayer is a follow-up once its API is verified on a real device.
#pragma warning disable CS0618

namespace MusicFinder.Infrastructure.Audio;

/// <summary>
/// WASAPI shared-mode output (never exclusive, so a DJ's sound card stays available to Traktor and VirtualDJ). All state changes happen under one lock.
/// A chosen device that is missing is an error: the preview never falls back to the Windows default, because that could be the PA.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiPreviewPlayer : IPreviewPlayer
{
    private readonly object _gate = new();
    private readonly ILogger<WasapiPreviewPlayer>? _log;
    private PreviewStatus _status = PreviewStatus.Idle;
    private WasapiOut? _output;
    private PreviewSource? _source;
    private VolumeSampleProvider? _volumeStage;
    private Timer? _timer;
    private int _generation;
    private float _volume = 0.5f;
    private bool _disposed;

    public WasapiPreviewPlayer(ILogger<WasapiPreviewPlayer>? log = null) => _log = log;

    public event EventHandler<PreviewStatus>? StateChanged;
    public event EventHandler<TimeSpan>? PositionChanged;

    public string? DeviceId { get; set; }

    public PreviewStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public float Volume
    {
        get { lock (_gate) return _volume; }
        set
        {
            lock (_gate)
            {
                _volume = Math.Clamp(value, 0f, 1f);
                if (_volumeStage is not null) _volumeStage.Volume = _volume;
            }
        }
    }

    public Task PlayAsync(PreviewTrack track, CancellationToken ct)
    {
        int generation;
        string? deviceId;
        Held held;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            generation = ++_generation;
            held = TakeOutputLocked();
            _status = PreviewStateMachine.Requested(_status, track);
            deviceId = DeviceId;
        }
        held.Close(_log);
        Raise();
        return Task.Run(() => Start(track, generation, deviceId), ct);
    }

    private void Start(PreviewTrack track, int generation, string? deviceId)
    {
        PreviewSource? source = null;
        WasapiOut? output = null;
        try
        {
            source = PreviewFileOpener.Open(track.FullPath);
            using var enumerator = new MMDeviceEnumerator();
            var device = ResolveDevice(enumerator, deviceId);
            ISampleProvider samples = source.Reader.ToSampleProvider();
            var volumeStage = new VolumeSampleProvider(samples) { Volume = Volume };
            output = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
            output.Init(volumeStage.ToWaveProvider());

            lock (_gate)
            {
                if (_disposed || generation != _generation) { output.Dispose(); source.Dispose(); return; }   // a newer request won
                _source = source;
                _output = output;
                _volumeStage = volumeStage;
                volumeStage.Volume = _volume;
                output.PlaybackStopped += (_, e) => OnPlaybackStopped(output, generation, e);
                output.Play();
                _status = PreviewStateMachine.Started(_status, source.Reader.TotalTime);
                _timer = new Timer(_ => Tick(generation), null, 250, 250);
            }
            Raise();
        }
        catch (Exception ex)
        {
            output?.Dispose();
            source?.Dispose();
            lock (_gate)
            {
                if (_disposed || generation != _generation) return;
                _status = PreviewStateMachine.Failed(_status, ex is PreviewOpenException or DeviceUnavailableException ? ex.Message : "The preview could not start: " + ex.Message);
            }
            _log?.LogInformation(ex, "Preview of {Path} failed", track.FullPath);
            Raise();
        }
    }

    private static MMDevice ResolveDevice(MMDeviceEnumerator enumerator, string? deviceId)
    {
        try
        {
            if (string.IsNullOrEmpty(deviceId)) return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var device = enumerator.GetDevice(deviceId);
            if (device.State != DeviceState.Active) throw new DeviceUnavailableException("The chosen preview output is not available. Pick another in Settings.");
            return device;
        }
        catch (DeviceUnavailableException) { throw; }
        catch (Exception) when (!string.IsNullOrEmpty(deviceId))
        {
            throw new DeviceUnavailableException("The chosen preview output is not available. Pick another in Settings.");
        }
        catch (Exception)
        {
            throw new DeviceUnavailableException("There is no audio output to play on.");
        }
    }

    private void OnPlaybackStopped(WasapiOut output, int generation, StoppedEventArgs e)
    {
        Held held;
        lock (_gate)
        {
            if (_disposed || generation != _generation || !ReferenceEquals(output, _output)) return;   // we stopped it ourselves
            held = TakeOutputLocked();
            _status = e.Exception is null
                ? PreviewStateMachine.Ended(_status)
                : PreviewStateMachine.Failed(_status, "Playback stopped: " + e.Exception.Message);
        }
        Task.Run(() => held.Close(_log));                       // this runs on the output's own thread, which must not close itself
        Raise();
    }

    private void Tick(int generation)
    {
        TimeSpan position;
        lock (_gate)
        {
            if (_disposed || generation != _generation || _source is null || _status.State != PreviewState.Playing) return;
            try { position = _source.Reader.CurrentTime; }
            catch (ObjectDisposedException) { return; }
            _status = PreviewStateMachine.Moved(_status, position);
        }
        PositionChanged?.Invoke(this, position);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_output is null || _status.State != PreviewState.Playing) return;
            _output.Pause();
            _status = PreviewStateMachine.Paused(_status);
        }
        Raise();
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_output is null || _status.State != PreviewState.Paused) return;
            _output.Play();
            _status = PreviewStateMachine.Resumed(_status);
        }
        Raise();
    }

    public void Stop()
    {
        Held held;
        lock (_gate)
        {
            if (_status.State is PreviewState.Idle or PreviewState.Ended) return;
            _generation++;                                      // a request still opening the file must not start afterwards
            held = TakeOutputLocked();
            _status = PreviewStateMachine.Stopped(_status);
        }
        held.Close(_log);
        Raise();
    }

    public void Seek(TimeSpan position)
    {
        TimeSpan moved;
        lock (_gate)
        {
            if (_source is null || _status.State is not (PreviewState.Playing or PreviewState.Paused)) return;
            var target = PreviewStateMachine.Moved(_status, position).Position;
            try { _source.Reader.CurrentTime = target; }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException) { return; }
            _status = PreviewStateMachine.Moved(_status, target);
            moved = target;
        }
        PositionChanged?.Invoke(this, moved);
    }

    public void ReleaseIfPlaying(string fullPath)
    {
        lock (_gate)
        {
            if (_status.Track is null || !string.Equals(_status.Track.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) return;
            if (_status.State is PreviewState.Idle or PreviewState.Ended or PreviewState.Failed) return;
        }
        Stop();
    }

    /// <summary>Detaches the output and file from the player. Caller holds the lock and closes them with <see cref="Held.Close"/> after leaving it,
    /// because stopping WASAPI waits for its thread and that thread's handler takes the lock.</summary>
    private Held TakeOutputLocked()
    {
        _timer?.Dispose();
        _timer = null;
        var held = new Held(_output, _source);
        _output = null;
        _source = null;
        _volumeStage = null;
        return held;
    }

    private readonly record struct Held(WasapiOut? Output, PreviewSource? Source)
    {
        public void Close(ILogger? log)
        {
            try { Output?.Stop(); } catch (Exception ex) { log?.LogDebug(ex, "Stopping the preview output"); }
            Output?.Dispose();
            Source?.Dispose();
        }
    }

    private void Raise()
    {
        PreviewStatus snapshot;
        lock (_gate) snapshot = _status;
        StateChanged?.Invoke(this, snapshot);
    }

    public void Dispose()
    {
        Held held;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            held = TakeOutputLocked();
            _status = PreviewStatus.Idle;
        }
        held.Close(_log);
    }

    private sealed class DeviceUnavailableException(string message) : Exception(message);
}

/// <summary>Lists the active render endpoints. Enumerating happens only when asked (Settings), never at startup.</summary>
[SupportedOSPlatform("windows")]
public sealed class MmPreviewOutputDevices : IPreviewOutputDevices
{
    public Task<IReadOnlyList<PreviewDevice>> ListAsync(CancellationToken ct) => Task.Run<IReadOnlyList<PreviewDevice>>(() =>
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try { defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch (Exception) { /* no default endpoint */ }
            var list = new List<PreviewDevice>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                ct.ThrowIfCancellationRequested();
                list.Add(new PreviewDevice(device.ID, device.FriendlyName, device.ID == defaultId));
                device.Dispose();
            }
            return list;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return []; }
    }, ct);
}

/// <summary>For platforms without WASAPI: every preview ends with a message.</summary>
public sealed class NullPreviewPlayer : IPreviewPlayer
{
    private PreviewStatus _status = PreviewStatus.Idle;
    public PreviewStatus Status => _status;
    public float Volume { get; set; } = 0.5f;
    public string? DeviceId { get; set; }
    public event EventHandler<PreviewStatus>? StateChanged;
    public event EventHandler<TimeSpan>? PositionChanged { add { } remove { } }

    public Task PlayAsync(PreviewTrack track, CancellationToken ct)
    {
        _status = PreviewStateMachine.Failed(PreviewStateMachine.Requested(_status, track), "Preview needs Windows.");
        StateChanged?.Invoke(this, _status);
        return Task.CompletedTask;
    }

    public void Pause() { }
    public void Resume() { }
    public void Stop() { }
    public void Seek(TimeSpan position) { }
    public void ReleaseIfPlaying(string fullPath) { }
    public void Dispose() { }
}

/// <summary>For platforms without WASAPI.</summary>
public sealed class NoPreviewDevices : IPreviewOutputDevices
{
    public Task<IReadOnlyList<PreviewDevice>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PreviewDevice>>([]);
}

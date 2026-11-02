using System.Runtime.Versioning;
using NAudio.MediaFoundation;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Audio;

/// <summary>
/// Reads a window of a track as mono float samples at the analysis sample rate. Every decoder shares this pipeline: the format-specific part
/// only supplies a reader; this part seeks to the middle of the track, averages the channels, resamples and stops after the window.
/// </summary>
internal static class DecodePipeline
{
    public static DecodeResult Run(WaveStream reader, AudioDecodeRequest request, string decoder, CancellationToken ct)
    {
        var format = reader.WaveFormat;
        var sourceSeconds = reader.TotalTime.TotalSeconds;
        if (sourceSeconds < 0.5 && reader.Length == 0) return DecodeResult.Fail("The file contains no audio.");

        // The middle of a long track skips intros and outros; a short track is read whole.
        var window = Math.Min(request.MaxSeconds, sourceSeconds > 0 ? sourceSeconds : request.MaxSeconds);
        var start = request.CentreWindow ? Math.Max(0, (sourceSeconds - window) / 2) : Math.Max(0, request.StartSeconds);
        var skipByReading = false;
        if (start > 0.5)
        {
            try { reader.CurrentTime = TimeSpan.FromSeconds(start); }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or IOException)
            {
                skipByReading = true;                          // a reader that cannot seek: read and discard up to the start
            }
        }

        ISampleProvider samples = reader.ToSampleProvider();
        if (format.Channels > 1) samples = new MonoMix(samples);
        if (samples.WaveFormat.SampleRate != request.TargetSampleRate)
            samples = new WdlResamplingSampleProvider(samples, request.TargetSampleRate);

        var wanted = (int)(window * request.TargetSampleRate);
        var buffer = new float[wanted];
        var filled = 0;
        var toSkip = skipByReading ? (long)(start * request.TargetSampleRate) : 0;
        var chunk = new float[request.TargetSampleRate];          // one second at a time, so cancellation is quick
        while (filled < wanted)
        {
            ct.ThrowIfCancellationRequested();
            var read = samples.Read(chunk.AsSpan(0, Math.Min(chunk.Length, toSkip > 0 ? (int)Math.Min(toSkip, chunk.Length) : wanted - filled)));
            if (read <= 0) break;
            if (toSkip > 0) { toSkip -= read; continue; }
            Array.Copy(chunk, 0, buffer, filled, read);
            filled += read;
        }

        if (filled < request.TargetSampleRate)              // under a second: nothing to analyse
            return DecodeResult.Fail("The file contains no audio that could be read.");
        if (filled < wanted) Array.Resize(ref buffer, filled);
        return DecodeResult.Ok(new DecodedAudio(buffer, request.TargetSampleRate, sourceSeconds, format.SampleRate, format.Channels, decoder));
    }

    /// <summary>The average of the channels of each frame.</summary>
    private sealed class MonoMix : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _scratch = new float[0];

        public MonoMix(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            var needed = buffer.Length * _channels;
            if (_scratch.Length < needed) _scratch = new float[needed];
            var read = _source.Read(_scratch.AsSpan(0, needed));
            var frames = read / _channels;
            for (var i = 0; i < frames; i++)
            {
                float sum = 0;
                for (var c = 0; c < _channels; c++) sum += _scratch[i * _channels + c];
                buffer[i] = sum / _channels;
            }
            return frames;
        }
    }
}

/// <summary>Windows Media Foundation: MP3, AAC/M4A and (on Windows 10 and later) FLAC. Needs the codec to be present in this Windows edition.</summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationAudioDecoder : IAudioDecoder
{
    private static readonly object Gate = new();
    private static bool _started;

    public Task<DecodeResult> DecodeAsync(string path, AudioDecodeRequest request, CancellationToken ct) =>
        Task.Run(() =>
        {
            try
            {
                lock (Gate)
                {
                    if (!_started) { MediaFoundationApi.Startup(); _started = true; }
                }
                using var reader = new MediaFoundationReader(path);
                return DecodePipeline.Run(reader, request, "Windows Media Foundation", ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return DecodeResult.Fail(Describe(path, ex));
            }
        }, ct);

    private static string Describe(string path, Exception ex)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ex is System.Runtime.InteropServices.COMException or InvalidOperationException
            ? $"Windows could not decode this {ext} file (the codec may be missing or the file damaged)."
            : "The file could not be read: " + ex.Message;
    }
}

/// <summary>WAV and AIFF, read without any Windows codec.</summary>
public sealed class WaveAiffAudioDecoder : IAudioDecoder
{
    public Task<DecodeResult> DecodeAsync(string path, AudioDecodeRequest request, CancellationToken ct) =>
        Task.Run(() =>
        {
            try
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                using WaveStream reader = ext is ".aif" or ".aiff" ? new AiffFileReader(path) : new WaveFileReader(path);
                return DecodePipeline.Run(reader, request, ext is ".aif" or ".aiff" ? "AIFF reader" : "WAV reader", ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return DecodeResult.Fail("The file could not be read as audio: " + ex.Message);
            }
        }, ct);
}

/// <summary>Ogg Vorbis, with a managed decoder (Windows has none). Ogg Opus and Ogg FLAC are not supported.</summary>
public sealed class VorbisAudioDecoder : IAudioDecoder
{
    public Task<DecodeResult> DecodeAsync(string path, AudioDecodeRequest request, CancellationToken ct) =>
        Task.Run(() =>
        {
            try
            {
                using var reader = new VorbisWaveReader(path);
                return DecodePipeline.Run(reader, request, "Ogg Vorbis reader", ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return DecodeResult.Fail("This Ogg file could not be decoded (only Ogg Vorbis is supported, not Opus or Ogg FLAC): " + ex.Message);
            }
        }, ct);
}

/// <summary>Chooses the decoder by file extension; any file a decoder cannot read ends as a message, never as an exception.</summary>
public sealed class AudioDecoderRouter : IAudioDecoder
{
    private readonly IAudioDecoder _mediaFoundation;
    private readonly IAudioDecoder _wave;
    private readonly IAudioDecoder _vorbis;

    public AudioDecoderRouter()
        : this(OperatingSystem.IsWindows() ? new MediaFoundationAudioDecoder() : new UnsupportedDecoder(), new WaveAiffAudioDecoder(), new VorbisAudioDecoder()) { }

    public AudioDecoderRouter(IAudioDecoder mediaFoundation, IAudioDecoder wave, IAudioDecoder vorbis)
    {
        _mediaFoundation = mediaFoundation;
        _wave = wave;
        _vorbis = vorbis;
    }

    public async Task<DecodeResult> DecodeAsync(string path, AudioDecodeRequest request, CancellationToken ct)
    {
        if (!File.Exists(path)) return DecodeResult.Fail("The file no longer exists.");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".wav" or ".aif" or ".aiff":
                var first = await _wave.DecodeAsync(path, request, ct);
                // An exotic WAV (ADPCM, a-law...) is still something Windows may know.
                return first.Audio is null && ext == ".wav" ? await _mediaFoundation.DecodeAsync(path, request, ct) is { Audio: not null } second ? second : first : first;
            case ".ogg" or ".oga":
                return await _vorbis.DecodeAsync(path, request, ct);
            case ".mp3" or ".m4a" or ".aac" or ".mp4" or ".flac" or ".wma":
                return await _mediaFoundation.DecodeAsync(path, request, ct);
            default:
                return DecodeResult.Fail($"MusicFinder cannot analyse {ext} files.");
        }
    }

    private sealed class UnsupportedDecoder : IAudioDecoder
    {
        public Task<DecodeResult> DecodeAsync(string path, AudioDecodeRequest request, CancellationToken ct) =>
            Task.FromResult(DecodeResult.Fail("Analysing this kind of file needs Windows."));
    }
}

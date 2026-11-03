using System.Runtime.Versioning;
using NAudio.MediaFoundation;
using NAudio.Vorbis;
using NAudio.Wave;

namespace MusicFinder.Infrastructure.Audio;

/// <summary>An opened audio file: the reader and the stream under it. Disposing releases the file.</summary>
public sealed class PreviewSource : IDisposable
{
    private readonly Stream _file;

    internal PreviewSource(WaveStream reader, Stream file)
    {
        Reader = reader;
        _file = file;
    }

    public WaveStream Reader { get; }

    public void Dispose()
    {
        Reader.Dispose();
        _file.Dispose();
    }
}

/// <summary>
/// Opens a track for listening. The file is opened read-only and shared for reading, writing and deleting, so Traktor, VirtualDJ and Explorer are never
/// blocked and an outside rename does not interrupt the sound. (MusicFinder's own tag writes and renames release the preview first.)
/// </summary>
[SupportedOSPlatform("windows")]
public static class PreviewFileOpener
{
    private static readonly object Gate = new();
    private static bool _started;

    public static string? Unsupported(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".m4a" or ".aac" or ".mp4" or ".flac" or ".wma" or ".wav" or ".aif" or ".aiff" or ".ogg" or ".oga"
            ? null
            : $"MusicFinder cannot play {Path.GetExtension(path)} files.";

    /// <summary>Throws <see cref="PreviewOpenException"/> with a short message the user can read.</summary>
    public static PreviewSource Open(string path)
    {
        if (!File.Exists(path)) throw new PreviewOpenException("The file no longer exists.");
        if (Unsupported(path) is { } why) throw new PreviewOpenException(why);
        var ext = Path.GetExtension(path).ToLowerInvariant();

        Stream? file = null;
        try
        {
            file = OpenStream(path);
            WaveStream reader;
            switch (ext)
            {
                case ".wav" or ".aif" or ".aiff":
                    try { reader = ext == ".wav" ? new WaveFileReader(file) : new AiffFileReader(file); }
                    catch (Exception) when (ext == ".wav")
                    {
                        file.Dispose();                                   // an exotic WAV (ADPCM, a-law): Windows may still know it
                        file = OpenStream(path);
                        reader = MediaFoundation(file);
                    }
                    break;
                case ".ogg" or ".oga":
                    reader = new VorbisWaveReader(file);
                    break;
                default:
                    reader = MediaFoundation(file);
                    break;
            }
            return new PreviewSource(reader, file);
        }
        catch (PreviewOpenException) { file?.Dispose(); throw; }
        catch (Exception ex)
        {
            file?.Dispose();
            throw new PreviewOpenException(Describe(ext, ex));
        }
    }

    private static FileStream OpenStream(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

    private static WaveStream MediaFoundation(Stream file)
    {
        lock (Gate)
        {
            if (!_started) { MediaFoundationApi.Startup(); _started = true; }
        }
        return new StreamMediaFoundationReader(file);
    }

    private static string Describe(string ext, Exception ex) =>
        ex is System.Runtime.InteropServices.COMException or InvalidOperationException
            ? $"Windows could not decode this {ext} file (the codec may be missing or the file damaged)."
            : ext is ".ogg" or ".oga"
                ? "This Ogg file could not be decoded (only Ogg Vorbis is supported, not Opus or Ogg FLAC)."
                : "The file could not be read: " + ex.Message;
}

public sealed class PreviewOpenException(string message) : Exception(message);

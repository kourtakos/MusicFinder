namespace MusicFinder.IntegrationTests;

/// <summary>
/// Generates minimal but structurally valid audio files at test time, so no audio is ever committed.
/// TagLib only needs valid container structure to read and write tags.
/// </summary>
internal static class Fixtures
{
    /// <summary>30 empty MPEG-1 Layer III frames (128 kbps, 44.1 kHz, stereo): FF FB 90 00 + zero payload, 417 bytes each.</summary>
    public static string MakeMp3(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        for (var i = 0; i < 30; i++) fs.Write(frame);
        return path;
    }

    /// <summary>"fLaC" + one STREAMINFO block (44.1 kHz, stereo, 16-bit, no samples). Enough structure for tagging.</summary>
    public static string MakeFlac(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        fs.Write("fLaC"u8);
        fs.Write([0x80, 0x00, 0x00, 0x22]);                          // last-metadata-block flag, type 0 (STREAMINFO), length 34
        fs.Write([0x10, 0x00, 0x10, 0x00]);                          // min/max block size 4096
        fs.Write([0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);              // min/max frame size unknown
        fs.Write([0x0A, 0xC4, 0x42, 0xF0, 0x00, 0x00, 0x00, 0x00]);  // 44100 Hz, 2 channels, 16 bit, 0 samples
        fs.Write(new byte[16]);                                      // MD5
        return path;
    }

    /// <summary>A tiny 8 kHz mono WAV; <see cref="ArtworkTests.MakeWav"/> adds tags/pictures on top.</summary>
    public static string MakeWav(string path) => ArtworkTests.MakeWav(path);
}

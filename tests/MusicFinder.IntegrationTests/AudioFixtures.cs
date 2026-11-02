using System.Text;

namespace MusicFinder.IntegrationTests;

/// <summary>Real audio files generated on the fly (WAV, FLAC, MP3): the decoders are tested against files, not against mocks.</summary>
internal static class AudioFixtures
{
    /// <summary>A sine of the given frequency; <paramref name="seconds"/> long.</summary>
    public static float[] Sine(double hz, double seconds, int rate, double amplitude = 0.5)
    {
        var a = new float[(int)(seconds * rate)];
        for (var i = 0; i < a.Length; i++) a[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
        return a;
    }

    /// <summary>16-bit PCM WAV; mono samples are copied to every channel (the right channel slightly quieter, so the two differ).</summary>
    public static string WriteWav(string path, float[] mono, int rate, int channels = 2)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs, Encoding.ASCII);
        var dataBytes = mono.Length * channels * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in mono)
            for (var c = 0; c < channels; c++)
                w.Write((short)Math.Clamp(s * (c == 0 ? 1.0 : 0.9) * short.MaxValue, short.MinValue, short.MaxValue));
        return path;
    }

    /// <summary>
    /// A minimal FLAC file: 16-bit mono, 4096-sample blocks, every block stored verbatim (no compression). Valid FLAC, written here
    /// because no FLAC encoder is available to the tests.
    /// </summary>
    public static string WriteFlac(string path, float[] mono, int rate)
    {
        var block = 4096;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        fs.Write("fLaC"u8);

        // STREAMINFO (the only metadata block, and the last one).
        var info = new byte[34];
        info[0] = (byte)(block >> 8); info[1] = (byte)block;          // minimum block size
        info[2] = (byte)(block >> 8); info[3] = (byte)block;          // maximum block size
        // minimum/maximum frame size: 0 = unknown (bytes 4..9)
        var total = (long)mono.Length;
        var packed = ((long)rate << 44) | ((long)0 << 41) | ((long)15 << 36) | total;   // 20 bits rate, 3 bits channels-1, 5 bits bps-1, 36 bits samples
        for (var i = 0; i < 8; i++) info[10 + i] = (byte)(packed >> (56 - 8 * i));
        // MD5 signature left as zero = unknown (bytes 18..33)
        fs.WriteByte(0x80);                                           // last-metadata-block flag + type 0 (STREAMINFO)
        fs.WriteByte(0); fs.WriteByte(0); fs.WriteByte(34);
        fs.Write(info);

        var frameNumber = 0;
        for (var start = 0; start < mono.Length; start += block, frameNumber++)
        {
            var count = Math.Min(block, mono.Length - start);
            using var frame = new MemoryStream();
            frame.WriteByte(0xFF); frame.WriteByte(0xF8);               // sync code + fixed block size
            var blockCode = count == block ? 0b1100 : 0b0111;           // 4096, or "8 bit (count-1) follows at the end of the header" for the last short block
            var rateCode = rate switch { 44100 => 0b1001, 48000 => 0b1010, 22050 => 0b0110, 32000 => 0b1000, _ => 0b0000 };
            frame.WriteByte((byte)((blockCode << 4) | rateCode));
            frame.WriteByte((byte)((0b0000 << 4) | (0b100 << 1)));     // mono, 16 bits per sample
            WriteUtf8(frame, frameNumber);
            if (count != block) frame.WriteByte((byte)(count - 1));
            if (rateCode == 0) { frame.WriteByte((byte)(rate / 1000)); }  // rate in kHz when it has no code (only whole kHz is supported here)
            frame.WriteByte(Crc8(frame.ToArray()));
            frame.WriteByte(0x02);                                      // subframe: verbatim, no wasted bits
            for (var i = 0; i < count; i++)
            {
                var v = (short)Math.Clamp(mono[start + i] * short.MaxValue, short.MinValue, short.MaxValue);
                frame.WriteByte((byte)(v >> 8)); frame.WriteByte((byte)v);
            }
            var crc = Crc16(frame.ToArray());
            frame.WriteByte((byte)(crc >> 8)); frame.WriteByte((byte)crc);
            frame.WriteTo(fs);
        }
        return path;
    }

    private static void WriteUtf8(Stream s, int value)
    {
        if (value < 0x80) { s.WriteByte((byte)value); return; }
        if (value < 0x800) { s.WriteByte((byte)(0xC0 | (value >> 6))); s.WriteByte((byte)(0x80 | (value & 0x3F))); return; }
        s.WriteByte((byte)(0xE0 | (value >> 12))); s.WriteByte((byte)(0x80 | ((value >> 6) & 0x3F))); s.WriteByte((byte)(0x80 | (value & 0x3F)));
    }

    private static byte Crc8(byte[] data)
    {
        byte crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x07) : (byte)(crc << 1);
        }
        return crc;
    }

    private static ushort Crc16(byte[] data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x8005) : (ushort)(crc << 1);
        }
        return crc;
    }

    /// <summary>The dominant frequency of the first second of a clip (Hz), by a plain FFT.</summary>
    public static double DominantHz(float[] samples, int rate, int fromSample = 0)
    {
        const int size = 8192;
        var fft = new MusicFinder.Application.Analysis.Fft(size);
        var mags = new double[size / 2 + 1];
        var span = samples.AsSpan(fromSample, Math.Min(size, samples.Length - fromSample));
        fft.Magnitudes(span, MusicFinder.Application.Analysis.Fft.Hann(size), mags);
        return Array.IndexOf(mags, mags.Max()) * (double)rate / size;
    }
}

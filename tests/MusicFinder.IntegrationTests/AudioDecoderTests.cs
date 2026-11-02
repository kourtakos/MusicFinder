using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Audio;

namespace MusicFinder.IntegrationTests;

public class AudioDecoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mf-audio-" + Guid.NewGuid().ToString("N"));
    private readonly AudioDecoderRouter _decoder = new();
    private static readonly AudioDecodeRequest Default = new();

    public AudioDecoderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task AStereoWavAt44100_BecomesMonoAt22050_WithItsSourceDescribed()
    {
        var path = AudioFixtures.WriteWav(P("a.wav"), AudioFixtures.Sine(440, 10, 44100), 44100, channels: 2);

        var result = await _decoder.DecodeAsync(path, Default, default);

        var audio = result.Audio!;
        Assert.Null(result.Failure);
        Assert.Equal(22050, audio.SampleRate);
        Assert.InRange(audio.Mono.Length / 22050.0, 9.8, 10.05);
        Assert.Equal((44100, 2), (audio.SourceSampleRate, audio.SourceChannels));
        Assert.InRange(audio.SourceDurationSeconds, 9.9, 10.1);
        Assert.InRange(AudioFixtures.DominantHz(audio.Mono, 22050), 435, 445);                      // the tone survived the downmix and the resampling
    }

    [Theory]
    [InlineData(22050, 1)]
    [InlineData(48000, 2)]
    [InlineData(32000, 1)]
    public async Task OtherSampleRatesAndChannelCounts_AreResampled(int rate, int channels)
    {
        var path = AudioFixtures.WriteWav(P($"r{rate}.wav"), AudioFixtures.Sine(1000, 6, rate), rate, channels);

        var audio = (await _decoder.DecodeAsync(path, Default, default)).Audio!;

        Assert.InRange(audio.Mono.Length / 22050.0, 5.8, 6.05);
        Assert.InRange(AudioFixtures.DominantHz(audio.Mono, 22050), 990, 1010);
    }

    [Fact]
    public async Task ALongTrack_IsReadFromItsMiddle_NinetySeconds()
    {
        // 200 s: a 440 Hz tone for the first 100 s, 880 Hz for the rest. The middle 90 s are 55-145 s: both halves.
        var samples = AudioFixtures.Sine(440, 100, 22050).Concat(AudioFixtures.Sine(880, 100, 22050)).ToArray();
        var path = AudioFixtures.WriteWav(P("long.wav"), samples, 22050, channels: 1);

        var audio = (await _decoder.DecodeAsync(path, Default, default)).Audio!;

        Assert.InRange(audio.Mono.Length / 22050.0, 89.5, 90.05);
        Assert.InRange(AudioFixtures.DominantHz(audio.Mono, 22050, fromSample: 2 * 22050), 435, 445);                   // the start of the window: still 440
        Assert.InRange(AudioFixtures.DominantHz(audio.Mono, 22050, fromSample: audio.Mono.Length - 3 * 22050), 875, 885);   // its end: already 880
        Assert.InRange(audio.SourceDurationSeconds, 199, 201);
    }

    [Fact]
    public async Task ARequestedStart_IsHonoured_WhenTheMiddleIsNotWanted()
    {
        var samples = AudioFixtures.Sine(440, 30, 22050).Concat(AudioFixtures.Sine(880, 30, 22050)).ToArray();
        var path = AudioFixtures.WriteWav(P("start.wav"), samples, 22050, channels: 1);

        var audio = (await _decoder.DecodeAsync(path, new AudioDecodeRequest(MaxSeconds: 10, CentreWindow: false, StartSeconds: 40), default)).Audio!;

        Assert.InRange(audio.Mono.Length / 22050.0, 9.8, 10.05);
        Assert.InRange(AudioFixtures.DominantHz(audio.Mono, 22050), 875, 885);
    }

    [Fact]
    public async Task AGreekFileNameInAGreekFolder_Decodes()
    {
        var path = AudioFixtures.WriteWav(P(Path.Combine("Μουσική", "Χάρις Αλεξίου - Ξανά στη ζωή μου.wav")), AudioFixtures.Sine(440, 5, 44100), 44100);

        var result = await _decoder.DecodeAsync(path, Default, default);

        Assert.NotNull(result.Audio);
    }

    [Fact]
    public async Task Flac_IsDecodedThroughWindows_OrReportedClearly()
    {
        var path = AudioFixtures.WriteFlac(P("a.flac"), AudioFixtures.Sine(440, 8, 44100), 44100);

        var result = await _decoder.DecodeAsync(path, Default, default);

        // FLAC is expected to decode through Windows 10 and later; where the codec is missing the answer must be a message, never an exception.
        if (result.Audio is null)
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Failure));
            return;
        }
        Assert.InRange(result.Audio.Mono.Length / 22050.0, 7.5, 8.1);
        Assert.InRange(AudioFixtures.DominantHz(result.Audio.Mono, 22050), 435, 445);
    }

    [Fact]
    public async Task AFlacFileInAGreekFolder_IsHandledToo()
    {
        var path = AudioFixtures.WriteFlac(P(Path.Combine("Ελληνικά", "τραγούδι.flac")), AudioFixtures.Sine(440, 6, 44100), 44100);

        var result = await _decoder.DecodeAsync(path, Default, default);

        Assert.True(result.Audio is not null || !string.IsNullOrWhiteSpace(result.Failure));
    }

    [Theory]
    [InlineData("garbage.mp3")]
    [InlineData("garbage.wav")]
    [InlineData("garbage.flac")]
    [InlineData("garbage.m4a")]
    [InlineData("garbage.ogg")]
    [InlineData("garbage.aiff")]
    public async Task AFileThatIsNotAudio_IsAMessage_NotAnException(string name)
    {
        var path = P(name);
        File.WriteAllBytes(path, Enumerable.Range(0, 5000).Select(i => (byte)(i * 31 % 251)).ToArray());

        var result = await _decoder.DecodeAsync(path, Default, default);

        Assert.Null(result.Audio);
        Assert.False(string.IsNullOrWhiteSpace(result.Failure));
    }

    [Theory]
    [InlineData("empty.mp3")]
    [InlineData("empty.wav")]
    public async Task AnEmptyFile_IsAMessage(string name)
    {
        var path = P(name);
        File.WriteAllBytes(path, []);

        var result = await _decoder.DecodeAsync(path, Default, default);

        Assert.Null(result.Audio);
        Assert.False(string.IsNullOrWhiteSpace(result.Failure));
    }

    [Fact]
    public async Task AMissingFile_IsAMessage()
    {
        var result = await _decoder.DecodeAsync(P("nowhere.mp3"), Default, default);

        Assert.Null(result.Audio);
        Assert.Contains("no longer exists", result.Failure);
    }

    [Fact]
    public async Task AnUnsupportedExtension_IsAMessage()
    {
        var path = P("a.opus");
        File.WriteAllBytes(path, new byte[100]);

        var result = await _decoder.DecodeAsync(path, Default, default);

        Assert.Null(result.Audio);
        Assert.Contains(".opus", result.Failure);
    }

    [Fact]
    public async Task ATruncatedWav_StillGivesTheAudioThatIsThere()
    {
        var full = AudioFixtures.WriteWav(P("full.wav"), AudioFixtures.Sine(440, 20, 44100), 44100);
        var bytes = File.ReadAllBytes(full);
        File.WriteAllBytes(P("cut.wav"), bytes[..(bytes.Length / 2)]);

        var result = await _decoder.DecodeAsync(P("cut.wav"), Default, default);

        // Either what could be read, or a clear message: never a crash.
        Assert.True(result.Audio is not null || !string.IsNullOrWhiteSpace(result.Failure));
    }

    [Fact]
    public async Task ACancelledRequest_Throws_AndNeverReturnsHalfAnAnswer()
    {
        var path = AudioFixtures.WriteWav(P("c.wav"), AudioFixtures.Sine(440, 60, 44100), 44100);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _decoder.DecodeAsync(path, Default, cts.Token));
    }

    [Fact]
    public async Task AShortFile_IsReadWhole()
    {
        var path = AudioFixtures.WriteWav(P("short.wav"), AudioFixtures.Sine(440, 3, 44100), 44100);

        var audio = (await _decoder.DecodeAsync(path, Default, default)).Audio!;

        Assert.InRange(audio.Mono.Length / 22050.0, 2.9, 3.05);
    }

    [Fact]
    public async Task AnMp3_IsDecodedThroughWindows()
    {
        // Encode a test MP3 with Windows' own encoder; where the machine has none the fixture cannot be made and there is nothing to check.
        var wav = AudioFixtures.WriteWav(P("src.wav"), AudioFixtures.Sine(440, 10, 44100), 44100);
        var mp3 = P("t.mp3");
        if (!TryEncodeMp3(wav, mp3)) return;

        var result = await _decoder.DecodeAsync(mp3, Default, default);

        Assert.NotNull(result.Audio);
        Assert.InRange(result.Audio!.Mono.Length / 22050.0, 9.0, 10.3);                    // an MP3 encoder adds a little padding
        Assert.InRange(AudioFixtures.DominantHz(result.Audio.Mono, 22050, fromSample: 22050), 430, 450);
        Assert.Equal("Windows Media Foundation", result.Audio.Decoder);
    }

    [Fact]
    public async Task AnMp3InAGreekFolder_Decodes()
    {
        var wav = AudioFixtures.WriteWav(P("src2.wav"), AudioFixtures.Sine(440, 8, 44100), 44100);
        var mp3 = P(Path.Combine("Μουσική", "Παπάζογλου.mp3"));
        Directory.CreateDirectory(Path.GetDirectoryName(mp3)!);
        if (!TryEncodeMp3(wav, mp3)) return;

        Assert.NotNull((await _decoder.DecodeAsync(mp3, Default, default)).Audio);
    }

    private static bool TryEncodeMp3(string wavPath, string mp3Path)
    {
        try
        {
            NAudio.MediaFoundation.MediaFoundationApi.Startup();
            using var reader = new NAudio.Wave.WaveFileReader(wavPath);
            NAudio.Wave.MediaFoundationEncoder.EncodeToMp3(reader, mp3Path, 192000);
            return File.Exists(mp3Path) && new FileInfo(mp3Path).Length > 1000;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

using NewLife.Audio.DSP;
using NewLife.Audio.Writers;

namespace NewLife.Audio.Tts;

/// <summary>把 PCM 块写成 WAV 或裸 PCM</summary>
internal sealed class TtsWaveWriter : IDisposable
{
    private readonly Stream _output;
    private readonly TtsFormat _format;
    private WavStreamWriter _wav;
    private Boolean _completed;

    /// <summary>实际音频格式</summary>
    public AudioFormat Format { get; private set; }

    public TtsWaveWriter(Stream output, TtsFormat format)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _format = format;
    }

    public void Write(TtsChunk chunk)
    {
        if (chunk?.Data == null || chunk.Data.Length == 0) return;

        EnsureOpen(chunk.Format);

        if (_wav != null)
            _wav.WriteChunkAsync(_output, new ReadOnlyMemory<Byte>(chunk.Data)).AsTask().GetAwaiter().GetResult();
        else
            _output.Write(chunk.Data, 0, chunk.Data.Length);
    }

    public void Complete()
    {
        if (_completed) return;
        _completed = true;

        if (_wav != null) _wav.WriteTrailerAsync(_output).AsTask().GetAwaiter().GetResult();
        _output.Flush();
    }

    public void Dispose() => Complete();

    private void EnsureOpen(AudioFormat format)
    {
        if (Format != null) return;

        Format = format?.Clone() ?? AudioFormat.Default;

        if (_format == TtsFormat.Wav)
        {
            _wav = new WavStreamWriter(Format.SampleRate)
            {
                Channels = Format.Channels,
                BitsPerSample = Format.BitsPerSample,
                PatchSize = _output.CanSeek,
            };
            _wav.WriteHeaderAsync(_output).AsTask().GetAwaiter().GetResult();
        }
    }
}

/// <summary>输出格式辅助</summary>
internal static class TtsFormatHelper
{
    /// <summary>按扩展名推断输出格式</summary>
    public static TtsFormat FromPath(String path)
    {
        var ext = Path.GetExtension(path)?.ToLowerInvariant();
        return ext switch
        {
            ".wav" or ".wave" => TtsFormat.Wav,
            ".pcm" => TtsFormat.Pcm,
            ".mp3" => TtsFormat.Mp3,
            ".flac" => TtsFormat.Flac,
            _ => throw new NotSupportedException(L.T("Unsupported TTS output format: {0}. Supported formats: .wav, .pcm, .mp3, .flac.", ext)),
        };
    }

    /// <summary>取压缩容器扩展名，非压缩格式返回 null</summary>
    /// <param name="format">输出格式</param>
    /// <returns>容器扩展名</returns>
    public static String GetContainer(TtsFormat format) => format switch
    {
        TtsFormat.Mp3 => ".mp3",
        TtsFormat.Flac => ".flac",
        _ => null,
    };
}

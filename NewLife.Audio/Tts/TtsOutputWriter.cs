using NewLife.Audio.DSP;
using NewLife.Audio.Streaming;

namespace NewLife.Audio.Tts;

/// <summary>把 TTS 输出的 PCM 块写成目标格式：WAV/裸 PCM 直写，MP3/FLAC 走压缩编码管线</summary>
/// <remarks>
/// 压缩格式首次写入时才建管线，因为输入采样率/声道要等引擎产出第一个分块才知道；
/// 管线内部完成重采样、声道转换、编码与容器写出，调用方只负责按块喂数据。
/// </remarks>
internal sealed class TtsOutputWriter : IDisposable
{
    private readonly Stream _output;
    private readonly TtsFormat _format;
    private TtsWaveWriter _wave;
    private AudioEncodePipeline _pipeline;
    private Boolean _completed;

    /// <summary>实际音频格式</summary>
    public AudioFormat Format { get; private set; }

    /// <summary>以输出流与目标格式初始化写器</summary>
    /// <param name="output">输出流</param>
    /// <param name="format">目标格式</param>
    public TtsOutputWriter(Stream output, TtsFormat format)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _format = format;
    }

    /// <summary>写入一个 PCM 分块</summary>
    /// <param name="chunk">合成分块</param>
    public void Write(TtsChunk chunk)
    {
        if (chunk?.Data == null || chunk.Data.Length == 0) return;

        EnsureOpen(chunk.Format);

        if (_pipeline != null)
            _pipeline.Write(chunk.Data);
        else
            _wave.Write(chunk);
    }

    /// <summary>完成写入，冲刷编码器与容器尾部</summary>
    public void Complete()
    {
        if (_completed) return;
        _completed = true;

        _pipeline?.Complete();
        _wave?.Complete();
        _output.Flush();
    }

    /// <summary>释放资源，未完成时先收尾</summary>
    public void Dispose()
    {
        try
        {
            Complete();
        }
        finally
        {
            _pipeline?.Dispose();
            _wave?.Dispose();
        }
    }

    private void EnsureOpen(AudioFormat format)
    {
        if (Format != null) return;

        Format = format?.Clone() ?? AudioFormat.Default;

        var container = TtsFormatHelper.GetContainer(_format);
        if (container == null)
        {
            _wave = new TtsWaveWriter(_output, _format);
            return;
        }

        _pipeline = new AudioEncodePipeline(_output, Format, new AudioEncodeOptions
        {
            Container = container,
            // 输出流由 TtsClient 负责关闭
            LeaveOpen = true,
        });
    }
}

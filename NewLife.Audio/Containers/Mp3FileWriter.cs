using NewLife.Audio.Codecs;
using NewLife.Audio.DSP;
using NewLife.Audio.Writers;

namespace NewLife.Audio.Containers;

/// <summary>MP3 文件写入器</summary>
/// <remarks>
/// 两种用法：
/// <list type="bullet">
/// <item>写入已编码帧：<see cref="WriteFrame"/> 直接透传 MP3 帧数据，配合 <see cref="WriteMetadata"/> 在最前面写入 ID3v2.4 标签。</item>
/// <item>写入 PCM：<see cref="WritePcm"/> 内部调用 <see cref="Mp3Codec"/> 的 Layer III 编码器按帧压缩，自动处理跨调用的半帧缓冲。</item>
/// </list>
/// </remarks>
public class Mp3FileWriter : IAudioContainerWriter, IAudioPcmSink
{
    #region 字段
    private readonly Stream _stream;
    private readonly AudioFormat _format;
    private readonly Mp3StreamWriter _writer;
    private readonly Int32 _bitrate;
    private AudioMetadata _metadata;
    private Boolean _tagWritten;
    private Boolean _completed;
    private Mp3Layer3Encoder _encoder;
    private Byte[] _pending = [];
    private Int32 _pendingLength;
    #endregion

    #region 属性
    /// <summary>音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>目标比特率（kbps），仅 PCM 编码时生效</summary>
    public Int32 Bitrate => _bitrate;
    #endregion

    #region 构造
    /// <summary>初始化 MP3 文件写入器（帧透传模式）</summary>
    /// <param name="stream">输出流</param>
    /// <param name="format">音频格式</param>
    public Mp3FileWriter(Stream stream, AudioFormat format) : this(stream, format, 0) { }

    /// <summary>初始化 MP3 文件写入器</summary>
    /// <param name="stream">输出流</param>
    /// <param name="format">音频格式</param>
    /// <param name="bitrate">PCM 编码比特率（kbps），传 0 表示按声道数自动选择（立体声 128、单声道 64）</param>
    public Mp3FileWriter(Stream stream, AudioFormat format, Int32 bitrate)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _writer = new Mp3StreamWriter();
        _bitrate = bitrate > 0 ? bitrate : format.Channels > 1 ? 128 : 64;

        _writer.WriteHeaderAsync(stream).AsTask().GetAwaiter().GetResult();
    }
    #endregion

    #region 方法
    /// <summary>写入元数据，在首个音频帧之前落成 ID3v2.4 标签</summary>
    /// <param name="metadata">音频元数据</param>
    public void WriteMetadata(AudioMetadata metadata)
    {
        _metadata = metadata;
        if (!_tagWritten) WriteTag();
    }

    /// <summary>写入一帧已编码的 MP3 数据</summary>
    /// <param name="frame">MP3 编码帧</param>
    public void WriteFrame(ReadOnlySpan<Byte> frame)
    {
        if (_completed) return;

        WriteTag();
        _writer.WriteChunkAsync(_stream, frame.ToArray(), default).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>写入 16 位小端交错 PCM 数据，内部按 MP3 帧边界压缩</summary>
    /// <param name="pcm">PCM 数据，长度可任意</param>
    /// <remarks>不足一帧的余量会缓存到下次调用或 <see cref="Flush"/> 时补齐</remarks>
    public void WritePcm(ReadOnlySpan<Byte> pcm)
    {
        if (_completed || pcm.Length == 0) return;

        WriteTag();
        var encoder = GetEncoder();
        var frameBytes = encoder.FrameSamples * _format.Channels * 2;

        var buffer = new Byte[_pendingLength + pcm.Length];
        if (_pendingLength > 0) Array.Copy(_pending, 0, buffer, 0, _pendingLength);
        pcm.CopyTo(buffer.AsSpan(_pendingLength));

        var total = buffer.Length;
        var offset = 0;
        var output = new Byte[encoder.MaxFrameBytes];
        while (offset + frameBytes <= total)
        {
            var len = encoder.EncodeFrame(buffer, offset, output);
            _writer.WriteChunkAsync(_stream, new ReadOnlyMemory<Byte>(output, 0, len), default).AsTask().GetAwaiter().GetResult();
            offset += frameBytes;
        }

        var remain = total - offset;
        if (remain > 0)
        {
            if (_pending.Length < frameBytes) _pending = new Byte[frameBytes];
            Array.Copy(buffer, offset, _pending, 0, remain);
        }
        _pendingLength = remain;
    }

    /// <summary>完成写入并刷新缓冲区</summary>
    public void Flush()
    {
        if (_completed) return;
        _completed = true;

        // 补齐最后一个半帧
        if (_pendingLength > 0 && _encoder != null)
        {
            var frameBytes = _encoder.FrameSamples * _format.Channels * 2;
            for (var i = _pendingLength; i < frameBytes; i++) _pending[i] = 0;

            var output = new Byte[_encoder.MaxFrameBytes];
            var len = _encoder.EncodeFrame(_pending, 0, output);
            _writer.WriteChunkAsync(_stream, new ReadOnlyMemory<Byte>(output, 0, len), default).AsTask().GetAwaiter().GetResult();
            _pendingLength = 0;
        }

        _writer.WriteTrailerAsync(_stream, default).AsTask().GetAwaiter().GetResult();

        // 追加 ID3v1 尾标签，兼容只识别 v1 的老播放器/车机
        var v1 = Mp3Id3Tag.WriteV1(_metadata);
        if (v1.Length > 0) _stream.Write(v1, 0, v1.Length);

        _stream.Flush();
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (!_completed)
        {
            try { Flush(); } catch { /* 流可能已关闭 */ }
        }
        _stream?.Dispose();
    }
    #endregion

    #region 辅助
    private Mp3Layer3Encoder GetEncoder() => _encoder ??= new Mp3Layer3Encoder(_format.SampleRate, _format.Channels, _bitrate);

    private void WriteTag()
    {
        if (_tagWritten) return;
        _tagWritten = true;

        var tag = Mp3Id3Tag.Write(_metadata);
        if (tag.Length > 0) _stream.Write(tag, 0, tag.Length);
    }
    #endregion
}

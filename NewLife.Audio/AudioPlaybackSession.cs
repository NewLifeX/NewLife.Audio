using System;
using System.Threading;
using NewLife.Audio.Codecs;
using NewLife.Audio.Containers;
using NewLife.Audio.Devices;
using NewLife.Audio.DSP;
using NewLife.Data;
using NewLife.Log;

namespace NewLife.Audio;

/// <summary>播放会话：逐帧解码、按水位写入、支持跳转</summary>
/// <remarks>
/// 把一个可定位的音频文件变成“顺序解码 → 播放器”的会话，供 <see cref="AudioPlayer"/> 使用。<br/>
/// 跳转在读取锁内完成：先定位容器帧，再清空播放器积压，随后由解码循环继续，因此不会与解码线程竞争。
/// Ogg Opus 的编码器前导样本（pre-skip）与尾部填充按容器信息裁剪。
/// </remarks>
internal sealed class AudioPlaybackSession : IDisposable
{
    private static readonly AudioCodecFactory _factory = new();

    private readonly IAudioPlayer _player;
    private readonly IAudioRender _render;
    private readonly IAudioContainerReader _reader;
    private readonly IAudioCodec _codec;
    private readonly Object _decodeOption;
    private readonly VorbisDecoder _vorbisDecoder;
    private readonly Single[] _vorbisSamples;
    private readonly Object _sync = new();

    private Int64 _skipRemaining;
    private Int64 _samplesWritten;
    private Int64 _totalSamples;
    private Double _positionBase;
    private Int64 _samplesSinceBase;
    private Boolean _disposed;

    /// <summary>构造播放会话（会初始化播放器）</summary>
    /// <param name="path">音频文件路径</param>
    /// <param name="player">播放器</param>
    /// <param name="render">带背压的写入端</param>
    /// <param name="deviceId">输出设备 ID</param>
    /// <param name="volume">音量，0~1</param>
    public AudioPlaybackSession(String path, IAudioPlayer player, IAudioRender render, String deviceId = null, Double volume = 1.0)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _reader = AudioContainerFactory.CreateReader(path);

        var codecType = _reader.CodecType;
        if (codecType != AVTypes.LPCM && _factory.Registry.IsSupported(codecType)) _codec = _factory.Registry.GetCodec(codecType);

        // MP4/M4A 的 AAC 帧没有 ADTS 头，必须把 esds 中的 AudioSpecificConfig 交给解码器
        if (_reader is Mp4FileReader mp4 && mp4.AacConfig != null) _decodeOption = mp4.AacConfig;

        // Ogg Vorbis 的音频包是裸 Vorbis 包，需要三头包初始化的有状态解码器
        if (_reader is OggFileReader ogg && ogg.CodecType == AVTypes.Vorbis &&
            ogg.VorbisIdentification != null && ogg.VorbisComment != null && ogg.VorbisSetup != null)
        {
            _vorbisDecoder = new VorbisDecoder(ogg.VorbisIdentification, ogg.VorbisComment, ogg.VorbisSetup);
            _vorbisSamples = new Single[_vorbisDecoder.MaxPacketSamples * _vorbisDecoder.Channels];
        }

        Format = _reader.Format ?? AudioFormat.Default;
        Duration = _reader.Duration;
        Volume = volume;

        _player.Init(Format, deviceId);

        ResetTrim();
    }

    #region 属性
    /// <summary>解码后的 PCM 格式</summary>
    public AudioFormat Format { get; }

    /// <summary>总时长（秒）</summary>
    public Double Duration { get; }

    /// <summary>音量，0~1</summary>
    public Double Volume { get; set; }

    /// <summary>当前位置（秒）</summary>
    public Double Position
    {
        get
        {
            var rate = Math.Max(1, Format.SampleRate);
            lock (_sync) return _positionBase + (Double)_samplesSinceBase / rate;
        }
    }
    #endregion

    #region 事件与动作
    /// <summary>PCM 数据可用（每块送播放器前触发，供频谱/波形使用）</summary>
    public event EventHandler<Byte[]> PcmDataAvailable;

    /// <summary>跳转到指定时间位置（秒）</summary>
    /// <param name="seconds">目标位置（秒），越界自动夹取</param>
    /// <returns>是否成功跳转</returns>
    public Boolean Seek(Double seconds)
    {
        lock (_sync)
        {
            var totalFrames = _reader.TotalFrames;
            if (totalFrames <= 0 || Duration <= 0) return false;

            var position = seconds;
            if (position < 0) position = 0;
            if (position > Duration) position = Duration;
            var index = (Int64)(position / Duration * totalFrames);
            if (index < 0) index = 0;
            if (index > totalFrames - 1) index = totalFrames - 1;

            _reader.SeekFrame(index);

            // 有状态编解码器必须复位，否则会带着旧状态解码
            if (_codec is OpusCodec opus) opus.Reset();
            _vorbisDecoder?.Reset();

            ResetTrim();

            _positionBase = (Double)index / totalFrames * Duration;
            _samplesSinceBase = 0;

            // 清掉播放器里已排队的旧数据，避免跳转后先听到过去的声音
            try
            {
                _player.Stop();
                _player.Play();
            }
            catch (Exception ex)
            {
                XTrace.Log.InfoPrefixed("AudioPlaybackSession: ", "Seek flush failed: {0}", ex.Message);
            }

            return true;
        }
    }

    /// <summary>开始播放并阻塞到播完或取消</summary>
    /// <param name="cancellationToken">取消标记</param>
    public void Play(CancellationToken cancellationToken = default)
    {
        var frameBytes = Math.Max(1, Format.Channels * Format.BytesPerSample);
        var chunkBytes = Math.Max(frameBytes, Format.ByteRate / 10 / frameBytes * frameBytes);
        var watermark = Math.Max(Format.ByteRate / 2, 1);
        var rate = Math.Max(1, Format.SampleRate);

        _player.Play();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Byte[] pcm;
                lock (_sync)
                {
                    var packet = _reader.ReadFrame();
                    if (packet == null) break;

                    pcm = Decode(packet);
                }

                if (pcm == null || pcm.Length == 0) continue;

                WritePcm(pcm, chunkBytes, watermark, frameBytes, cancellationToken);
            }

            // Vorbis 解码器还缓存最后一个重叠块，播放结束前补出
            var flush = DecodeFlush();
            if (flush != null && flush.Length > 0 && !cancellationToken.IsCancellationRequested)
                WritePcm(flush, chunkBytes, watermark, frameBytes, cancellationToken);

            // 等驱动把缓冲区播完
            while (_render.BufferedBytes > 0 && !cancellationToken.IsCancellationRequested) Thread.Sleep(20);
        }
        finally
        {
            try
            {
                _player.Stop();
            }
            catch
            {
                // 设备失效时 Stop 可能失败，忽略
            }
        }
    }
    #endregion

    #region 辅助
    /// <summary>解码一帧并做音量/裁剪处理（需在锁内调用）</summary>
    private Byte[] Decode(IPacket packet)
    {
        var pcm = _vorbisDecoder != null
            ? ConvertVorbis(_vorbisDecoder.DecodePacket(packet.GetSpan(), _vorbisSamples), _vorbisDecoder.Channels)
            : _codec != null ? _codec.ToPcm(packet.GetSpan(), _decodeOption).GetSpan().ToArray() : packet.GetSpan().ToArray();

        return ProcessPcm(pcm);
    }

    /// <summary>取出 Vorbis 解码器缓存的尾块</summary>
    /// <returns>处理后的 PCM；没有尾块时返回 null</returns>
    private Byte[] DecodeFlush()
    {
        if (_vorbisDecoder == null) return null;

        var count = _vorbisDecoder.Flush(_vorbisSamples);
        if (count <= 0) return null;

        return ProcessPcm(ConvertVorbis(count, _vorbisDecoder.Channels));
    }

    /// <summary>把 Vorbis 浮点样本转换为 16 位小端 PCM</summary>
    /// <param name="count">每声道样本数</param>
    /// <param name="channels">声道数</param>
    /// <returns>PCM 字节</returns>
    private Byte[] ConvertVorbis(Int32 count, Int32 channels)
    {
        var total = count * channels;
        var pcm = new Byte[total * 2];
        for (var i = 0; i < total; i++)
        {
            var value = _vorbisSamples[i] * 32767f;
            var sample = value <= -32768f ? Int16.MinValue : value >= 32767f ? Int16.MaxValue : (Int16)Math.Round(value);
            pcm[i * 2] = (Byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (Byte)((sample >> 8) & 0xFF);
        }

        return pcm;
    }

    /// <summary>对解码后的 PCM 做裁剪与音量处理</summary>
    /// <param name="pcm">PCM 字节</param>
    /// <returns>处理后的 PCM</returns>
    private Byte[] ProcessPcm(Byte[] pcm)
    {
        if (pcm.Length == 0) return pcm;

        pcm = Trim(pcm);
        if (pcm.Length == 0) return pcm;

        var volume = Volume;
        if (volume >= 0 && Math.Abs(volume - 1) > 0.0001) ApplyVolume(pcm, volume);

        return pcm;
    }

    /// <summary>把 PCM 按水位写入播放器</summary>
    private void WritePcm(Byte[] pcm, Int32 chunkBytes, Int32 watermark, Int32 frameBytes, CancellationToken cancellationToken)
    {
        // 频谱/波形订阅者拿独立数组，后续写入不再改动它
        PcmDataAvailable?.Invoke(this, pcm);

        for (var offset = 0; offset < pcm.Length; offset += chunkBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(chunkBytes, pcm.Length - offset);
            var span = pcm.AsSpan(offset, count);

            while (!_render.WaitForSpace(watermark, 500))
                cancellationToken.ThrowIfCancellationRequested();

            _render.Write(span);

            lock (_sync) _samplesSinceBase += count / frameBytes;
        }
    }

    /// <summary>按容器信息裁剪 pre-skip 与尾部填充</summary>
    private Byte[] Trim(Byte[] pcm)
    {
        var bytesPerSample = 2 * Math.Max(1, Format.Channels);

        if (_skipRemaining > 0)
        {
            var skipBytes = (Int64)Math.Min(pcm.Length, _skipRemaining * bytesPerSample);
            if (skipBytes > 0)
            {
                pcm = pcm.AsSpan((Int32)skipBytes).ToArray();
                _skipRemaining -= skipBytes / bytesPerSample;
            }
        }

        if (_totalSamples > 0)
        {
            var remaining = _totalSamples - _samplesWritten;
            if (remaining <= 0) return [];

            var maxBytes = (Int32)Math.Min(pcm.Length, remaining * bytesPerSample);
            if (maxBytes < pcm.Length) pcm = pcm.AsSpan(0, maxBytes).ToArray();
        }

        _samplesWritten += pcm.Length / bytesPerSample;

        return pcm;
    }

    private void ResetTrim()
    {
        _samplesWritten = 0;
        _skipRemaining = 0;
        _totalSamples = 0;

        if (_reader is OggFileReader ogg)
        {
            _skipRemaining = ogg.PreSkip;
            _totalSamples = ogg.TotalSamples;
        }
    }

    /// <summary>按音量缩放 16 位小端 PCM</summary>
    private static void ApplyVolume(Byte[] pcm, Double volume)
    {
        var gain = Math.Max(0, Math.Min(1, volume));
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (Int16)(pcm[i] | pcm[i + 1] << 8);
            var scaled = (Int32)(sample * gain);
            if (scaled > Int16.MaxValue) scaled = Int16.MaxValue;
            else if (scaled < Int16.MinValue) scaled = Int16.MinValue;

            pcm[i] = (Byte)(scaled & 0xFF);
            pcm[i + 1] = (Byte)((scaled >> 8) & 0xFF);
        }
    }

    /// <summary>释放会话</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _reader.Dispose();
    }
    #endregion
}

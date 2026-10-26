using NewLife;

namespace NewLife.Audio.Codecs;

/// <summary>Vorbis 流式解码器</summary>
/// <remarks>
/// 按 Vorbis I 规范实现：三个头包解析、floor/residue/声道耦合、IMDCT 与重叠相加。
/// 解码器为有状态对象，必须按包顺序调用 <see cref="DecodePacket"/>，最后调用 <see cref="Flush"/> 取出尾包。
/// 输出为交错浮点 PCM，值域约 [-1, 1]。
/// </remarks>
public sealed class VorbisDecoder
{
    private readonly VorbisSetup _setup;
    private readonly VorbisMdct _mdct = new();
    private readonly Single[][] _bufferA;
    private readonly Single[][] _bufferB;
    private Single[][] _previous;
    private Single[][] _current;
    private Int32 _previousStart;
    private Int32 _previousEnd;
    private Int32 _previousStop;
    private Boolean _hasPacket;

    /// <summary>使用三个 Vorbis 头包初始化解码器</summary>
    /// <param name="identification">标识头包</param>
    /// <param name="comment">注释头包</param>
    /// <param name="setup">设置头包</param>
    public VorbisDecoder(ReadOnlySpan<Byte> identification, ReadOnlySpan<Byte> comment, ReadOnlySpan<Byte> setup)
    {
        _setup = VorbisSetup.Read(identification, comment, setup);
        _bufferA = CreateBuffers(_setup.Channels, _setup.Block1Size);
        _bufferB = CreateBuffers(_setup.Channels, _setup.Block1Size);
        _previous = _bufferA;
        _current = _bufferB;
    }

    /// <summary>采样率</summary>
    public Int32 SampleRate => _setup.SampleRate;

    /// <summary>声道数</summary>
    public Int32 Channels => _setup.Channels;

    /// <summary>短块大小</summary>
    public Int32 Block0Size => _setup.Block0Size;

    /// <summary>长块大小</summary>
    public Int32 Block1Size => _setup.Block1Size;

    /// <summary>单包最大样本数（每声道）</summary>
    public Int32 MaxPacketSamples => _setup.Block1Size;

    /// <summary>重置解码状态</summary>
    public void Reset()
    {
        _previousStart = 0;
        _previousEnd = 0;
        _previousStop = 0;
        _hasPacket = false;
    }

    /// <summary>解码一个音频包</summary>
    /// <param name="packet">Vorbis 音频包</param>
    /// <param name="samples">交错浮点 PCM 输出，容量至少为 <see cref="Channels"/> × <see cref="MaxPacketSamples"/></param>
    /// <returns>写入的每声道样本数；首包返回 0</returns>
    public Int32 DecodePacket(ReadOnlySpan<Byte> packet, Span<Single> samples)
    {
        var reader = new VorbisBitReader(packet);
        if (reader.ReadBits(1) != 0) return 0;

        var modeIndex = (Int32)reader.ReadBits(_setup.ModeFieldBits);
        if (modeIndex >= _setup.Modes.Length) throw new InvalidDataException(L.T("The Vorbis mode index is invalid."));

        var mode = _setup.Modes[modeIndex];
        mode.ReadPacketInfo(ref reader, out var windowIndex, out var startIndex, out var validLength, out var totalLength);

        var blockSize = mode.BlockSize;
        var current = _current;
        mode.Mapping.DecodePacket(ref reader, blockSize, current, _mdct);

        var window = mode.GetWindow(windowIndex);
        for (var i = 0; i < blockSize; i++)
        {
            var factor = window[i];
            for (var channel = 0; channel < _setup.Channels; channel++)
                current[channel][i] *= factor;
        }

        if (!_hasPacket)
        {
            _hasPacket = true;
            _previous = current;
            _current = current == _bufferA ? _bufferB : _bufferA;
            _previousStart = validLength;
            _previousEnd = validLength;
            _previousStop = totalLength;
            return 0;
        }

        var previous = _previous;
        var nextIndex = startIndex;
        for (var previousIndex = _previousStart; previousIndex < _previousStop; previousIndex++, nextIndex++)
        {
            for (var channel = 0; channel < _setup.Channels; channel++)
                current[channel][nextIndex] += previous[channel][previousIndex];
        }

        var count = validLength - startIndex;
        if (count < 0) count = 0;
        if (samples.Length < count * _setup.Channels)
            throw new ArgumentException(L.T("The Vorbis output buffer is too small."), nameof(samples));

        var index = 0;
        for (var i = startIndex; i < startIndex + count; i++)
        {
            for (var channel = 0; channel < _setup.Channels; channel++)
                samples[index++] = current[channel][i];
        }

        _previous = current;
        _current = current == _bufferA ? _bufferB : _bufferA;
        _previousStart = validLength;
        _previousEnd = validLength;
        _previousStop = totalLength;

        return count;
    }

    /// <summary>取出最后一个包剩余的样本</summary>
    /// <param name="samples">交错浮点 PCM 输出</param>
    /// <returns>写入的每声道样本数</returns>
    public Int32 Flush(Span<Single> samples)
    {
        if (!_hasPacket) return 0;

        var count = _previousStop - _previousStart;
        if (count < 0) count = 0;
        if (samples.Length < count * _setup.Channels)
            throw new ArgumentException(L.T("The Vorbis output buffer is too small."), nameof(samples));

        var index = 0;
        for (var i = _previousStart; i < _previousStart + count; i++)
        {
            for (var channel = 0; channel < _setup.Channels; channel++)
                samples[index++] = _previous[channel][i];
        }

        _hasPacket = false;
        _previousStart = 0;
        _previousEnd = 0;
        _previousStop = 0;

        return count;
    }

    private static Single[][] CreateBuffers(Int32 channels, Int32 blockSize)
    {
        var buffers = new Single[channels][];
        for (var i = 0; i < channels; i++)
            buffers[i] = new Single[blockSize];

        return buffers;
    }
}

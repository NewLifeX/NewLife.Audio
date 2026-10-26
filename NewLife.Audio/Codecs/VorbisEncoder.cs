using NewLife;

namespace NewLife.Audio.Codecs;

/// <summary>Vorbis 流式编码器</summary>
/// <remarks>
/// 纯托管 Vorbis I 编码器：MDCT → floor1 包络 → residue 量化 → Huffman/比特流 → Ogg 包。
/// 编码器为有状态对象，按包顺序调用 <see cref="EncodePacket"/>，最后循环调用 <see cref="Flush"/> 取完尾包。
/// </remarks>
public sealed class VorbisEncoder : IDisposable
{
    private static readonly Byte[] _signature = [(Byte)'v', (Byte)'o', (Byte)'r', (Byte)'b', (Byte)'i', (Byte)'s'];
    private static readonly Int32[] _valueBookEntries = [16, 64, 256, 1024];
    private const Int32 PartitionSize = 32;
    private const Int32 ClassCount = 4;
    private const Int32 FloorCount = 4;
    private const Int32 FloorDimensions = 8;

    private readonly VorbisOptions _options;
    private readonly Int32 _channels;
    private readonly Int32 _blockSize;
    private readonly Int32 _hop;
    private readonly Int32 _spectrumSize;
    private readonly Int32 _residueEnd;
    private readonly Int32 _floorYBook = 0;
    private readonly Int32 _classBook = 1;
    private readonly Int32 _valueBookStart = 2;
    private readonly Int32[] _floorX;
    private readonly Single[] _window;
    private readonly VorbisMdct _mdct = new();
    private readonly Single[][] _blocks;
    private readonly Single[][] _time;
    private readonly Single[][] _spectrum;
    private readonly Single[] _partitionValues = new Single[PartitionSize];
    private readonly Byte[][] _headers;
    private readonly Queue<PacketInfo> _ready = new();
    private Single[] _pending = [];
    private Int32 _pendingCount;
    private Int32 _packetIndex;
    private Int32 _inputSamples;
    private Int32 _emittedSamples;
    private Boolean _flushed;

    /// <summary>使用编码参数初始化编码器</summary>
    /// <param name="options">编码参数</param>
    public VorbisEncoder(VorbisOptions options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        _channels = _options.Channels;
        _blockSize = _options.BlockSize1;
        _hop = _blockSize / 2;
        _spectrumSize = _hop;
        _residueEnd = Math.Max(PartitionSize, (Int32)(_spectrumSize * _channels * GetCutoff(_options.Quality)) / PartitionSize * PartitionSize);
        _floorX = BuildFloorX();
        _window = BuildWindow();
        _blocks = CreateBuffers(_channels, _blockSize);
        _time = CreateBuffers(_channels, _blockSize);
        _spectrum = CreateBuffers(_channels, _spectrumSize);
        _headers = BuildHeaders();
    }

    /// <summary>三个 Vorbis 头包（identification / comment / setup）</summary>
    public IReadOnlyList<Byte[]> Headers => _headers;

    /// <summary>最近一个音频包输出的每声道样本数</summary>
    public Int32 LastPacketSamples { get; private set; }

    /// <summary>编码一帧 PCM</summary>
    /// <param name="samples">交错浮点 PCM，值域约 [-1, 1]</param>
    /// <param name="sampleCountPerChannel">每声道样本数</param>
    /// <returns>Vorbis 音频包；暂无输出时返回空数组</returns>
    public Byte[] EncodePacket(ReadOnlySpan<Single> samples, Int32 sampleCountPerChannel)
    {
        if (sampleCountPerChannel < 0) throw new ArgumentOutOfRangeException(nameof(sampleCountPerChannel));
        if (samples.Length < sampleCountPerChannel * _channels)
            throw new ArgumentException(L.T("The Vorbis input buffer is too small."), nameof(samples));

        Append(samples, sampleCountPerChannel);
        _inputSamples += sampleCountPerChannel;

        while (_pendingCount >= _hop * _channels)
        {
            MovePendingToBlock();
            ProcessBlock();
            ShiftBlock();
        }

        return Dequeue();
    }

    /// <summary>刷新编码器，输出剩余音频包</summary>
    /// <returns>Vorbis 音频包；无输出时返回空数组</returns>
    public Byte[] Flush()
    {
        if (!_flushed)
        {
            _flushed = true;

            if (_pendingCount > 0)
            {
                var target = _hop * _channels;
                EnsurePending(target);
                for (var i = _pendingCount; i < target; i++) _pending[i] = 0;
                _pendingCount = target;
                MovePendingToBlock();
                ProcessBlock();
                ShiftBlock();
            }

            // 末块用零填充，驱动解码器输出最后一个输入块
            for (var channel = 0; channel < _channels; channel++)
                Array.Clear(_blocks[channel], _hop, _hop);

            ProcessBlock();
            ShiftBlock();
        }

        return Dequeue();
    }

    /// <summary>重置编码状态</summary>
    public void Reset()
    {
        _pendingCount = 0;
        _packetIndex = 0;
        _inputSamples = 0;
        _emittedSamples = 0;
        _flushed = false;
        _ready.Clear();
        foreach (var block in _blocks) Array.Clear(block, 0, block.Length);
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
    }

    private Byte[] Dequeue()
    {
        if (_ready.Count == 0) return [];

        var packet = _ready.Dequeue();
        LastPacketSamples = packet.Samples;

        return packet.Data;
    }

    private void Append(ReadOnlySpan<Single> samples, Int32 sampleCount)
    {
        var required = _pendingCount + sampleCount * _channels;
        EnsurePending(required);

        for (var i = 0; i < sampleCount; i++)
        {
            for (var channel = 0; channel < _channels; channel++)
                _pending[_pendingCount++] = samples[i * _channels + channel];
        }
    }

    private void EnsurePending(Int32 required)
    {
        if (_pending.Length >= required) return;

        var size = Math.Max(required, Math.Max(_hop * _channels, 1024));
        Array.Resize(ref _pending, size);
    }

    private void MovePendingToBlock()
    {
        for (var channel = 0; channel < _channels; channel++)
        {
            var destination = _blocks[channel];
            var offset = _hop;
            for (var i = 0; i < _hop; i++)
                destination[offset + i] = _pending[i * _channels + channel];
        }

        var consumed = _hop * _channels;
        Array.Copy(_pending, consumed, _pending, 0, _pendingCount - consumed);
        _pendingCount -= consumed;
    }

    private void ShiftBlock()
    {
        for (var channel = 0; channel < _channels; channel++)
            Array.Copy(_blocks[channel], _hop, _blocks[channel], 0, _hop);
    }

    private void ProcessBlock()
    {
        var floorY = new Int32[_channels];
        var floorAmplitude = new Single[_channels];

        for (var channel = 0; channel < _channels; channel++)
        {
            var time = _time[channel];
            var block = _blocks[channel];
            for (var i = 0; i < _blockSize; i++)
                time[i] = block[i] * _window[i];

            _mdct.Forward(time, _blockSize);
            var scale = 2f / _spectrumSize;
            for (var i = 0; i < _spectrumSize; i++)
                _spectrum[channel][i] = time[i] * scale;

            var max = 0f;
            for (var i = 0; i < _spectrumSize; i++)
            {
                var value = Math.Abs(_spectrum[channel][i]);
                if (value > max) max = value;
            }

            var residualMax = GetResidualMax(_options.Quality);
            floorY[channel] = max > 1e-10f ? VorbisFloor1.AmplitudeToY(max / residualMax * 1.05f) : 0;
            floorAmplitude[channel] = VorbisFloor1.GetAmplitude(floorY[channel]);
        }

        var writer = new VorbisBitWriter();
        writer.WriteBit(false);             // audio packet
        writer.WriteBit(true);              // previous window long
        writer.WriteBit(true);              // next window long

        for (var channel = 0; channel < _channels; channel++)
        {
            writer.WriteBit(true);          // floor present
            writer.WriteBits((UInt32)floorY[channel], 8);
            writer.WriteBits((UInt32)floorY[channel], 8);
            for (var i = 2; i < 2 + FloorCount * FloorDimensions; i++)
                writer.WriteBits(ReverseBits(0, 8), 8);
        }

        var partitionCount = _residueEnd / PartitionSize;
        for (var partition = 0; partition < partitionCount; partition++)
        {
            var max = 0f;
            for (var i = 0; i < PartitionSize; i++)
            {
                var flatIndex = partition * PartitionSize + i;
                var channel = flatIndex % _channels;
                var index = flatIndex / _channels;
                var value = index < _spectrumSize ? _spectrum[channel][index] / floorAmplitude[channel] : 0f;
                _partitionValues[i] = value;

                var abs = Math.Abs(value);
                if (abs > max) max = abs;
            }

            var classification = ChooseClass(max);
            writer.WriteBits(ReverseBits(classification, 2), 2);

            var entries = _valueBookEntries[classification];
            var bits = VorbisCodebook.ILog(entries - 1);
            var minimum = -entries / 2;
            var maximum = entries / 2 - 1;
            for (var i = 0; i < PartitionSize; i++)
            {
                var value = (Int32)Math.Round(_partitionValues[i]);
                if (value < minimum) value = minimum;
                if (value > maximum) value = maximum;

                writer.WriteBits(ReverseBits(value - minimum, bits), bits);
            }
        }

        var remaining = Math.Max(0, _inputSamples - _emittedSamples);
        var samples = _packetIndex == 0 ? 0 : Math.Min(_hop, remaining);
        _emittedSamples += samples;
        _packetIndex++;

        _ready.Enqueue(new PacketInfo { Data = writer.Finish(), Samples = samples });
    }

    private static Int32 ChooseClass(Single max)
    {
        for (var i = 0; i < _valueBookEntries.Length; i++)
        {
            if (max < _valueBookEntries[i] / 2) return i;
        }

        return _valueBookEntries.Length - 1;
    }

    private static Single GetResidualMax(Int32 quality) => quality switch
    {
        <= 1 => 8f,
        <= 3 => 16f,
        <= 5 => 32f,
        <= 7 => 64f,
        <= 9 => 128f,
        _ => 256f,
    };

    private static Single GetCutoff(Int32 quality) => quality switch
    {
        <= 1 => 0.25f,
        <= 3 => 0.45f,
        <= 5 => 0.65f,
        <= 7 => 0.80f,
        <= 9 => 0.92f,
        _ => 1.00f,
    };

    private Byte[][] BuildHeaders()
    {
        var identification = BuildIdentification();
        var comment = BuildComment();
        var setup = BuildSetup();

        return [identification, comment, setup];
    }

    private Byte[] BuildIdentification()
    {
        using var ms = new MemoryStream();
        ms.WriteByte(1);
        ms.Write(_signature, 0, _signature.Length);
        WriteUInt32(ms, 0);
        ms.WriteByte((Byte)_channels);
        WriteUInt32(ms, (UInt32)_options.SampleRate);
        WriteUInt32(ms, 0);
        WriteUInt32(ms, 0);
        WriteUInt32(ms, 0);
        ms.WriteByte((Byte)((Log2(_blockSize) << 4) | Log2(_options.BlockSize0)));
        ms.WriteByte(1);

        return ms.ToArray();
    }

    private static Byte[] BuildComment()
    {
        using var ms = new MemoryStream();
        ms.WriteByte(3);
        ms.Write(_signature, 0, _signature.Length);
        var vendor = System.Text.Encoding.UTF8.GetBytes("NewLife.Audio");
        WriteUInt32(ms, (UInt32)vendor.Length);
        ms.Write(vendor, 0, vendor.Length);
        WriteUInt32(ms, 0);
        ms.WriteByte(1);

        return ms.ToArray();
    }

    private Byte[] BuildSetup()
    {
        var writer = new VorbisBitWriter();
        writer.WriteBits(5, 8);
        writer.WriteBytes(_signature);

        var codebookCount = _valueBookStart + _valueBookEntries.Length;
        writer.WriteBits((UInt32)(codebookCount - 1), 8);

        WriteCodebook(writer, 1, 256, 8, 0, 0f, 0f, 0, null);
        WriteCodebook(writer, 1, ClassCount, 2, 0, 0f, 0f, 0, null);
        for (var i = 0; i < _valueBookEntries.Length; i++)
        {
            var entries = _valueBookEntries[i];
            var bits = VorbisCodebook.ILog(entries - 1);
            var multiplicands = new UInt32[entries];
            for (var j = 0; j < entries; j++) multiplicands[j] = (UInt32)j;

            WriteCodebook(writer, 1, entries, bits, 1, -entries / 2f, 1f, bits, multiplicands);
        }

        writer.WriteBits(0, 6);             // time transform count - 1
        writer.WriteBits(0, 16);            // transform type 0
        writer.WriteBits(0, 6);             // floor count - 1
        writer.WriteBits(1, 16);            // floor type 1
        WriteFloor1(writer);
        writer.WriteBits(0, 6);             // residue count - 1
        writer.WriteBits(2, 16);            // residue type 2
        WriteResidue(writer);
        writer.WriteBits(0, 6);             // mapping count - 1
        writer.WriteBits(0, 16);            // mapping type 0
        writer.WriteBit(false);             // no submaps
        writer.WriteBit(false);             // no coupling
        writer.WriteBits(0, 2);             // reserved
        writer.WriteBits(0, 8);             // unused time config
        writer.WriteBits(0, 8);             // floor 0
        writer.WriteBits(0, 8);             // residue 0
        writer.WriteBits(0, 6);             // mode count - 1
        writer.WriteBit(true);              // long block
        writer.WriteBits(0, 32);            // window/transform type 0
        writer.WriteBits(0, 8);             // mapping 0
        writer.WriteBit(true);              // framing

        return writer.Finish();
    }

    private void WriteFloor1(VorbisBitWriter writer)
    {
        writer.WriteBits(FloorCount, 5);
        for (var i = 0; i < FloorCount; i++) writer.WriteBits(0, 4); // class 0

        writer.WriteBits(FloorDimensions - 1, 3);
        writer.WriteBits(0, 2);                     // no subclasses
        writer.WriteBits((UInt32)(_floorYBook + 1), 8);
        writer.WriteBits(0, 2);                     // multiplier - 1
        var rangeBits = Log2(_spectrumSize);
        writer.WriteBits((UInt32)rangeBits, 4);
        for (var i = 2; i < _floorX.Length; i++)
            writer.WriteBits((UInt32)_floorX[i], rangeBits);
    }

    private void WriteResidue(VorbisBitWriter writer)
    {
        writer.WriteBits(0, 24);                            // begin
        writer.WriteBits((UInt32)_residueEnd, 24);          // end
        writer.WriteBits(PartitionSize - 1, 24);            // partition size - 1
        writer.WriteBits(ClassCount - 1, 6);                // classifications - 1
        writer.WriteBits((UInt32)_classBook, 8);

        for (var i = 0; i < ClassCount; i++)
        {
            writer.WriteBits(1, 3);                         // one stage, book 0
            writer.WriteBit(false);                         // no second stage
        }

        for (var i = 0; i < ClassCount; i++)
            writer.WriteBits((UInt32)(_valueBookStart + i), 8);
    }

    private static UInt32 ReverseBits(Int32 value, Int32 bits)
    {
        var result = 0U;
        for (var i = 0; i < bits; i++)
        {
            result = (result << 1) | ((UInt32)value & 1);
            value >>= 1;
        }

        return result;
    }

    private static void WriteCodebook(VorbisBitWriter writer, Int32 dimensions, Int32 entries, Int32 bits, Int32 mapType,
        Single minimum, Single delta, Int32 valueBits, UInt32[] multiplicands)
    {
        writer.WriteBits(0x564342, 24);
        writer.WriteBits((UInt32)dimensions, 16);
        writer.WriteBits((UInt32)entries, 24);
        writer.WriteBit(false);                 // unordered
        writer.WriteBit(false);                 // no unused entries
        for (var i = 0; i < entries; i++) writer.WriteBits((UInt32)(bits - 1), 5);

        writer.WriteBits((UInt32)mapType, 4);
        if (mapType == 0) return;

        writer.WriteBits(Float32Pack(minimum), 32);
        writer.WriteBits(Float32Pack(delta), 32);
        writer.WriteBits((UInt32)(valueBits - 1), 4);
        writer.WriteBit(false);                 // sequence_p
        for (var i = 0; i < multiplicands!.Length; i++)
            writer.WriteBits(multiplicands[i], valueBits);
    }

    private static UInt32 Float32Pack(Single value)
    {
        UInt32 sign = 0;
        if (value < 0)
        {
            sign = 0x80000000;
            value = -value;
        }

        var exponent = 0;
        while (value >= 2f)
        {
            value /= 2f;
            exponent++;
        }
        while (value < 1f && value > 0f)
        {
            value *= 2f;
            exponent--;
        }

        var mantissa = (UInt32)Math.Round(value * (1 << 20));
        if (mantissa >= 1U << 21)
        {
            mantissa >>= 1;
            exponent++;
        }

        // Vorbis float32 的尾数是 21 位整数，需把归一化的小数尾数换算回整数表示
        return sign | ((UInt32)(exponent - 20 + 788) << 21) | (mantissa & 0x1FFFFF);
    }

    private Int32[] BuildFloorX()
    {
        var count = 2 + FloorCount * FloorDimensions;
        var range = _spectrumSize;
        var x = new Int32[count];
        x[0] = 0;
        x[1] = range;
        for (var i = 2; i < count; i++)
            x[i] = (Int32)Math.Round((Double)(i - 1) * range / (count - 1));

        return x;
    }

    private Single[] BuildWindow()
    {
        var window = new Single[_blockSize];
        for (var i = 0; i < window.Length; i++)
        {
            var x = (Single)Math.Sin(Math.PI * (i + 0.5) / _blockSize);
            window[i] = (Single)Math.Sin(Math.PI / 2 * x * x);
        }

        return window;
    }

    private static Single[][] CreateBuffers(Int32 channels, Int32 size)
    {
        var buffers = new Single[channels][];
        for (var i = 0; i < channels; i++)
            buffers[i] = new Single[size];

        return buffers;
    }

    private static void WriteUInt32(Stream stream, UInt32 value)
    {
        stream.WriteByte((Byte)(value & 0xFF));
        stream.WriteByte((Byte)((value >> 8) & 0xFF));
        stream.WriteByte((Byte)((value >> 16) & 0xFF));
        stream.WriteByte((Byte)((value >> 24) & 0xFF));
    }

    private static Int32 Log2(Int32 value)
    {
        var result = 0;
        while ((value >>= 1) > 0) result++;
        return result;
    }

    private sealed class PacketInfo
    {
        public Byte[] Data = [];
        public Int32 Samples;
    }
}

using System.Security.Cryptography;
using System.Text;
using NewLife;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>FLAC 写入选项</summary>
public class FlacWriterOptions
{
    /// <summary>每帧块大小（每声道样本数），默认 4096</summary>
    public Int32 BlockSize { get; set; } = 4096;

    /// <summary>压缩级别 0~8。0 为 verbatim（不做预测），1~8 依次搜索更高阶固定预测</summary>
    public Int32 CompressionLevel { get; set; } = 5;

    /// <summary>写入 PADDING 块字节数，便于后续原地改标签，0 表示不写</summary>
    public Int32 PaddingBytes { get; set; } = 8192;

    /// <summary>是否写入 SEEKTABLE</summary>
    public Boolean WriteSeekTable { get; set; } = true;

    /// <summary>SEEKTABLE 预留的定位点数量</summary>
    public Int32 SeekPointCount { get; set; } = 10;
}

/// <summary>FLAC 文件写入器。写入标准 fLaC 容器：STREAMINFO + SEEKTABLE + VORBIS_COMMENT + PADDING + 音频帧</summary>
/// <remarks>
/// 帧编码按 FLAC 规范实现：固定预测（order 0~4）+ Rice 残差编码，压缩级别 0 退化为 verbatim 子帧。<br/>
/// 可定位流在 <see cref="Flush"/> 时回填 STREAMINFO 的总采样数、帧长极值与 MD5，并回填 SEEKTABLE 偏移；
/// 不可定位流写入"未知"占位值。<br/>
/// 输入 PCM 固定为 16 位小端交错；<see cref="WriteMetadata"/> 必须在写入音频数据之前调用。
/// </remarks>
/// <example>
/// <code>
/// using var writer = new FlacFileWriter(stream, new AudioFormat { SampleRate = 44100, Channels = 2, BitsPerSample = 16 });
/// writer.WriteMetadata(new AudioMetadata { Title = "Demo", Artist = "NewLife" });
/// writer.WriteFrame(pcm16);
/// writer.Flush();
/// </code>
/// </example>
public class FlacFileWriter : IAudioContainerWriter
{
    #region 常量
    private const Byte BlockTypeStreamInfo = 0;
    private const Byte BlockTypePadding = 1;
    private const Byte BlockTypeSeekTable = 3;
    private const Byte BlockTypeVorbisComment = 4;
    private const String VendorString = "NewLife.Audio";
    #endregion

    #region 字段
    private readonly Stream _stream;
    private readonly AudioFormat _format;
    private readonly FlacWriterOptions _options;
    private readonly Byte[] _pcmBuffer;
    private readonly List<Int64> _seekSamples = [];
    private readonly List<Int64> _seekOffsets = [];
    private readonly MD5 _md5 = MD5.Create();
    private Int32 _pcmCount;
    private Int64 _totalSamples;
    private Int64 _frameNumber;
    private Int64 _streamInfoOffset;
    private Int64 _seekTableOffset;
    private Int64 _minFrameSize;
    private Int64 _maxFrameSize;
    private Boolean _headerWritten;
    private Boolean _closed;
    #endregion

    #region 属性
    /// <summary>音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>已写入的每声道样本数</summary>
    public Int64 TotalSamples => _totalSamples;

    /// <summary>元数据（在写入首个音频块前设置）</summary>
    public AudioMetadata Metadata { get; private set; }
    #endregion

    #region 构造
    /// <summary>创建 FLAC 写入器</summary>
    /// <param name="stream">输出流</param>
    /// <param name="format">音频格式（16 位小端交错 PCM）</param>
    /// <param name="options">写入选项</param>
    public FlacFileWriter(Stream stream, AudioFormat format, FlacWriterOptions options = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _format = format?.Clone() ?? throw new ArgumentNullException(nameof(format));
        _options = options ?? new FlacWriterOptions();

        if (_format.BitsPerSample != 16) throw new NotSupportedException(L.T("The requested audio format is not supported: {0}.", _format.BitsPerSample));
        if (_format.Channels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(format), "FLAC supports 1~8 channels");
        if (_format.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(format), "Sample rate must be positive");

        var blockSize = _options.BlockSize is > 0 and <= 65535 ? _options.BlockSize : 4096;
        _options.BlockSize = blockSize;
        _pcmBuffer = new Byte[blockSize * _format.Channels * 2];
    }
    #endregion

    #region 写入
    /// <summary>写入元数据（Vorbis comment）</summary>
    /// <param name="metadata">音频元数据</param>
    /// <exception cref="InvalidOperationException">已在写入音频数据后调用</exception>
    public void WriteMetadata(AudioMetadata metadata)
    {
        if (metadata == null) return;
        if (_headerWritten) throw new InvalidOperationException("Metadata must be written before audio data.");

        Metadata = metadata;
    }

    /// <summary>写入 PCM 数据（16 位小端交错），内部按块编码为 FLAC 帧</summary>
    /// <param name="frame">PCM 字节</param>
    public void WriteFrame(ReadOnlySpan<Byte> frame)
    {
        ThrowIfClosed();
        EnsureHeader();

        while (frame.Length > 0)
        {
            var space = _pcmBuffer.Length - _pcmCount;
            var take = Math.Min(space, frame.Length);
            frame.Slice(0, take).CopyTo(_pcmBuffer.AsSpan(_pcmCount));
            _md5.TransformBlock(_pcmBuffer, _pcmCount, take, null, 0);
            _pcmCount += take;
            frame = frame.Slice(take);

            if (_pcmCount == _pcmBuffer.Length) EncodeBlock();
        }
    }

    /// <summary>完成写入：编码残余数据并回填文件头信息</summary>
    public void Flush()
    {
        if (_closed) return;

        EnsureHeader();
        if (_pcmCount > 0) EncodeBlock();

        _md5.TransformFinalBlock([], 0, 0);
        var hash = _md5.Hash ?? new Byte[16];

        if (_stream.CanSeek) PatchStreamInfo(hash);
        _stream.Flush();
        _closed = true;
    }

    /// <summary>释放写入器</summary>
    public void Dispose()
    {
        Flush();
        _md5.Dispose();
    }
    #endregion

    #region 文件头
    private void EnsureHeader()
    {
        if (_headerWritten) return;

        _headerWritten = true;
        _stream.Write(Encoding.ASCII.GetBytes("fLaC"), 0, 4);

        _streamInfoOffset = _stream.Position;
        WriteStreamInfo();

        if (_options.WriteSeekTable)
        {
            _seekTableOffset = _stream.Position;
            WriteSeekTable();
        }

        WriteVorbisComment();

        if (_options.PaddingBytes > 0) WritePadding(_options.PaddingBytes);
        else WriteBlockHeader(BlockTypePadding, 0, true);
    }

    private void WriteStreamInfo()
    {
        var block = new Byte[34];
        FillStreamInfo(block, _format, (UInt16)_options.BlockSize, 0, 0, 0, null);

        WriteBlockHeader(BlockTypeStreamInfo, 34, false);
        _stream.Write(block, 0, block.Length);
    }

    private void WriteSeekTable()
    {
        var count = Math.Max(1, _options.SeekPointCount);
        WriteBlockHeader(BlockTypeSeekTable, count * 18, false);

        var placeholder = new Byte[18];
        for (var i = 0; i < 18; i++) placeholder[i] = 0xFF;

        for (var i = 0; i < count; i++)
        {
            _seekSamples.Add(-1);
            _seekOffsets.Add(-1);
            _stream.Write(placeholder, 0, placeholder.Length);
        }
    }

    private void WriteVorbisComment()
    {
        var body = FlacMetadataBlocks.BuildVorbisComment(Metadata, VendorString);

        WriteBlockHeader(BlockTypeVorbisComment, body.Length, false);
        _stream.Write(body, 0, body.Length);
    }

    private void WritePadding(Int32 bytes)
    {
        WriteBlockHeader(BlockTypePadding, bytes, true);

        var zeros = new Byte[Math.Min(bytes, 4096)];
        var remaining = bytes;
        while (remaining > 0)
        {
            var n = Math.Min(remaining, zeros.Length);
            _stream.Write(zeros, 0, n);
            remaining -= n;
        }
    }

    private void WriteBlockHeader(Byte type, Int32 length, Boolean last)
    {
        var header = new Byte[4];
        header[0] = (Byte)((last ? 0x80 : 0) | type);
        header[1] = (Byte)(length >> 16);
        header[2] = (Byte)(length >> 8);
        header[3] = (Byte)length;
        _stream.Write(header, 0, 4);
    }

    private void PatchStreamInfo(Byte[] hash)
    {
        var end = _stream.Position;

        var block = new Byte[34];
        FillStreamInfo(block, _format, (UInt16)_options.BlockSize, _minFrameSize, _maxFrameSize, _totalSamples, hash);

        _stream.Seek(_streamInfoOffset + 4, SeekOrigin.Begin);
        _stream.Write(block, 0, block.Length);

        PatchSeekTable();

        _stream.Seek(end, SeekOrigin.Begin);
    }

    private static void FillStreamInfo(Byte[] block, AudioFormat format, UInt16 blockSize, Int64 minFrame, Int64 maxFrame, Int64 totalSamples, Byte[] md5)
    {
        WriteBE16(block, 0, blockSize);
        WriteBE16(block, 2, blockSize);
        WriteBE24(block, 4, (Int32)Math.Min(minFrame, 0xFFFFFF));
        WriteBE24(block, 7, (Int32)Math.Min(maxFrame, 0xFFFFFF));

        var bits = format.BitsPerSample - 1;
        var rate = format.SampleRate;
        block[10] = (Byte)(rate >> 12);
        block[11] = (Byte)(rate >> 4);
        block[12] = (Byte)(((rate & 0x0F) << 4) | ((format.Channels - 1) << 1) | ((bits >> 4) & 0x01));
        block[13] = (Byte)(((bits & 0x0F) << 4) | (Byte)((totalSamples >> 32) & 0x0F));
        block[14] = (Byte)(totalSamples >> 24);
        block[15] = (Byte)(totalSamples >> 16);
        block[16] = (Byte)(totalSamples >> 8);
        block[17] = (Byte)totalSamples;

        if (md5 != null) Array.Copy(md5, 0, block, 18, Math.Min(16, md5.Length));
    }

    private void PatchSeekTable()
    {
        if (!_options.WriteSeekTable || _seekTableOffset <= 0) return;

        var count = Math.Max(1, _options.SeekPointCount);
        var data = new Byte[count * 18];
        for (var i = 0; i < data.Length; i++) data[i] = 0xFF;

        for (var i = 0; i < count && i < _seekSamples.Count; i++)
        {
            if (_seekSamples[i] < 0 || _seekOffsets[i] < 0) continue;

            var offset = i * 18;
            WriteBE64(data, offset, (UInt64)_seekSamples[i]);
            WriteBE64(data, offset + 8, (UInt64)_seekOffsets[i]);
            WriteBE16(data, offset + 16, (UInt16)_options.BlockSize);
        }

        var end = _stream.Position;
        _stream.Seek(_seekTableOffset + 4, SeekOrigin.Begin);
        _stream.Write(data, 0, data.Length);
        _stream.Seek(end, SeekOrigin.Begin);
    }
    #endregion

    #region 帧编码
    private void EncodeBlock()
    {
        var blockAlign = _format.Channels * 2;
        var samples = _pcmCount / blockAlign;
        if (samples <= 0)
        {
            _pcmCount = 0;
            return;
        }

        if (_options.WriteSeekTable && _seekSamples.Count < Math.Max(1, _options.SeekPointCount))
        {
            _seekSamples.Add(_totalSamples);
            _seekOffsets.Add(_stream.CanSeek ? _stream.Position : -1);
        }

        var frameStart = _stream.CanSeek ? _stream.Position : 0;
        var writer = new FlacBitWriter();

        // 帧头
        writer.Write(0x3FFE, 14);          // sync
        writer.Write(0, 1);                // reserved
        writer.Write(0, 1);                // blocking strategy: fixed
        writer.Write(samples <= 256 ? 6 : 7, 4);
        writer.Write(0, 4);                // sample rate: 取 STREAMINFO
        writer.Write(_format.Channels - 1, 4);
        writer.Write(GetSampleSizeCode(_format.BitsPerSample), 3);
        writer.Write(0, 1);                // reserved
        writer.WriteUtf8Number(_frameNumber);

        if (samples <= 256) writer.Write(samples - 1, 8);
        else writer.Write(samples - 1, 16);

        var header = writer.ToArray();
        writer.WriteByte(Crc8(header, 0, header.Length));

        // 子帧
        var channels = new Int32[samples];
        for (var ch = 0; ch < _format.Channels; ch++)
        {
            for (var i = 0; i < samples; i++)
            {
                var offset = (i * _format.Channels + ch) * 2;
                channels[i] = (Int16)(_pcmBuffer[offset] | _pcmBuffer[offset + 1] << 8);
            }

            WriteSubframe(writer, channels);
        }

        // 字节对齐 + CRC16
        writer.AlignToByte();
        var body = writer.ToArray();
        var crc = Crc16(body, 0, body.Length);
        writer.WriteByte((Byte)(crc >> 8));
        writer.WriteByte((Byte)crc);

        var frame = writer.ToArray();
        _stream.Write(frame, 0, frame.Length);

        if (_stream.CanSeek)
        {
            var size = _stream.Position - frameStart;
            if (_minFrameSize == 0 || size < _minFrameSize) _minFrameSize = size;
            if (size > _maxFrameSize) _maxFrameSize = size;
        }

        _totalSamples += samples;
        _frameNumber++;
        _pcmCount = 0;
    }

    private void WriteSubframe(FlacBitWriter writer, Int32[] samples)
    {
        var bestOrder = -1;
        var bestBits = _options.CompressionLevel > 0 ? Int64.MaxValue : (Int64)samples.Length * _format.BitsPerSample;
        var bestResidual = null as Int32[];
        var bestK = 0;

        if (_options.CompressionLevel > 0)
        {
            var maxOrder = Math.Min(4, _options.CompressionLevel);
            for (var order = 0; order <= maxOrder && order < samples.Length; order++)
            {
                var residual = ComputeResidual(samples, order);
                var k = SelectRiceParameter(residual, order);
                var bits = (Int64)order * _format.BitsPerSample + 8 + 2 + 4;
                for (var i = order; i < residual.Length; i++)
                {
                    var u = ZigZag(residual[i]);
                    bits += (u >> k) + 1 + k;
                }

                if (bits < bestBits)
                {
                    bestBits = bits;
                    bestOrder = order;
                    bestResidual = residual;
                    bestK = k;
                }
            }
        }

        writer.Write(0, 1);
        if (bestOrder < 0 || bestResidual == null)
        {
            // verbatim 子帧
            writer.Write(1, 6);
            writer.Write(0, 1);
            foreach (var sample in samples) writer.WriteSigned(sample, _format.BitsPerSample);
            return;
        }

        // 固定预测子帧
        writer.Write(8 + bestOrder, 6);
        writer.Write(0, 1);

        for (var i = 0; i < bestOrder; i++) writer.WriteSigned(samples[i], _format.BitsPerSample);

        writer.Write(0, 2);       // 分区数 = 1
        writer.Write(bestK, 4);   // Rice 参数
        for (var i = bestOrder; i < bestResidual.Length; i++)
        {
            var u = ZigZag(bestResidual[i]);
            var quotient = u >> bestK;
            // FLAC 的 unary 编码为 quotient 个 0 后跟一个 1
            for (var q = 0; q < quotient; q++) writer.Write(0, 1);
            writer.Write(1, 1);
            if (bestK > 0) writer.Write(u & ((1 << bestK) - 1), bestK);
        }
    }

    private static Int32[] ComputeResidual(Int32[] samples, Int32 order)
    {
        var residual = new Int32[samples.Length];
        for (var i = 0; i < order && i < samples.Length; i++) residual[i] = samples[i];

        for (var i = order; i < samples.Length; i++)
        {
            var prediction = order switch
            {
                0 => 0,
                1 => samples[i - 1],
                2 => 2 * samples[i - 1] - samples[i - 2],
                3 => 3 * samples[i - 1] - 3 * samples[i - 2] + samples[i - 3],
                _ => 4 * samples[i - 1] - 6 * samples[i - 2] + 4 * samples[i - 3] - samples[i - 4],
            };
            residual[i] = samples[i] - prediction;
        }

        return residual;
    }

    private static Int32 SelectRiceParameter(Int32[] residual, Int32 order)
    {
        var bestK = 0;
        var bestBits = Int64.MaxValue;

        for (var k = 0; k <= 14; k++)
        {
            var bits = 0L;
            for (var i = order; i < residual.Length; i++)
            {
                var u = ZigZag(residual[i]);
                bits += (u >> k) + 1 + k;
            }

            if (bits < bestBits)
            {
                bestBits = bits;
                bestK = k;
            }
        }

        return bestK;
    }

    /// <summary>把有符号残差映射为无符号（偶数正、奇数负）</summary>
    private static Int32 ZigZag(Int32 value) => (value << 1) ^ (value >> 31);
    #endregion

    #region 校验
    private static Byte Crc8(Byte[] data, Int32 offset, Int32 count)
    {
        Byte crc = 0;
        for (var i = 0; i < count; i++)
        {
            crc ^= data[offset + i];
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (Byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            }
        }
        return crc;
    }

    private static UInt16 Crc16(Byte[] data, Int32 offset, Int32 count)
    {
        UInt16 crc = 0;
        for (var i = 0; i < count; i++)
        {
            crc ^= (UInt16)(data[offset + i] << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (UInt16)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
            }
        }
        return crc;
    }
    #endregion

    #region 辅助
    private void ThrowIfClosed()
    {
        if (_closed) throw new ObjectDisposedException(nameof(FlacFileWriter));
    }

    private static void AddComment(List<String> comments, String key, String value)
    {
        if (!value.IsNullOrEmpty()) comments.Add($"{key}={value}");
    }

    /// <summary>帧头的样本位深编码（0 表示取 STREAMINFO）</summary>
    private static Int32 GetSampleSizeCode(Int32 bitsPerSample) => bitsPerSample switch
    {
        8 => 1,
        12 => 2,
        16 => 4,
        20 => 5,
        24 => 6,
        32 => 7,
        _ => 0,
    };

    private static void WriteBE16(Byte[] buffer, Int32 offset, UInt16 value)
    {
        buffer[offset] = (Byte)(value >> 8);
        buffer[offset + 1] = (Byte)value;
    }

    private static void WriteBE24(Byte[] buffer, Int32 offset, Int32 value)
    {
        buffer[offset] = (Byte)(value >> 16);
        buffer[offset + 1] = (Byte)(value >> 8);
        buffer[offset + 2] = (Byte)value;
    }

    private static void WriteBE64(Byte[] buffer, Int32 offset, UInt64 value)
    {
        for (var i = 0; i < 8; i++) buffer[offset + i] = (Byte)(value >> (56 - i * 8));
    }

    private static void WriteLE32(List<Byte> buffer, Int32 value)
    {
        buffer.Add((Byte)value);
        buffer.Add((Byte)(value >> 8));
        buffer.Add((Byte)(value >> 16));
        buffer.Add((Byte)(value >> 24));
    }
    #endregion

    #region 内部类型
    /// <summary>FLAC 比特写入器（高位在前）</summary>
    private sealed class FlacBitWriter
    {
        private Byte[] _data = new Byte[8192];
        private Int32 _pos;

        public void Write(Int32 value, Int32 count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                if (_pos >> 3 >= _data.Length) Array.Resize(ref _data, _data.Length * 2);

                if (((value >> i) & 1) != 0) _data[_pos >> 3] |= (Byte)(0x80 >> (_pos & 7));
                _pos++;
            }
        }

        public void WriteSigned(Int32 value, Int32 bits)
        {
            var limit = 1 << (bits - 1);
            if (value >= limit) value = limit - 1;
            if (value < -limit) value = -limit;

            Write(value & ((1 << bits) - 1), bits);
        }

        public void WriteByte(Byte value) => Write(value, 8);

        /// <summary>UTF-8 风格的可变长帧号（FLAC 帧头规定）</summary>
        public void WriteUtf8Number(Int64 value)
        {
            var v = (UInt64)value;
            if (v < 0x80)
            {
                Write((Int32)v, 8);
                return;
            }

            var count = v < 0x800 ? 2 : v < 0x10000 ? 3 : v < 0x200000 ? 4 : v < 0x4000000 ? 5 : 6;
            var prefix = (0xFF00 >> count) & 0xFF;          // 2→0xC0, 3→0xE0, 4→0xF0, 5→0xF8, 6→0xFC
            var firstBits = 7 - count;
            Write(prefix | (Int32)(v >> (6 * (count - 1))), 8);

            for (var i = count - 2; i >= 0; i--)
            {
                Write(0x80 | (Int32)((v >> (6 * i)) & 0x3F), 8);
            }

            _ = firstBits;
        }

        public void AlignToByte()
        {
            var remain = _pos & 7;
            if (remain != 0) _pos += 8 - remain;
        }

        public Byte[] ToArray()
        {
            var bytes = new Byte[(_pos + 7) >> 3];
            Array.Copy(_data, bytes, bytes.Length);
            return bytes;
        }
    }
    #endregion
}

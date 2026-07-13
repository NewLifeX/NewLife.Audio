using System.Text;
using NewLife.Buffers;
using NewLife.Data;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>MP4/M4A 文件读取器。解析 ISO BMFF (ISO 14496-12) 容器格式，提取 AAC 音频帧</summary>
/// <remarks>
/// MP4/ISO BMFF 是 box 结构的容器格式。每个 box：[size:4B][type:4B][data:size-8B]。
/// 读取器解析 ftyp→moov→mdat 层级，从 stbl 采样表定位每帧 AAC 数据。
/// </remarks>
public class Mp4FileReader : IAudioContainerReader
{
    private readonly Stream _stream;
    private AudioFormat _format;
    private AVTypes _codecType;
    private Int64 _totalFrames;
    private Int64 _currentFrame;
    private Int32 _timescale;

    // 采样表
    private UInt32[] _sampleSizes;
    private Int64[] _sampleOffsets;
    private Int32 _sampleCount;

    /// <summary>音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>编码类型</summary>
    public AVTypes CodecType => _codecType;

    /// <summary>总帧数</summary>
    public Int64 TotalFrames => _totalFrames;

    /// <summary>总时长（秒）</summary>
    public Double Duration => _totalFrames > 0 && _format?.SampleRate > 0
        ? (Double)_totalFrames * _format.SamplesPerFrame / _format.SampleRate
        : 0;

    /// <summary>元数据</summary>
    public AudioMetadata Metadata { get; } = new();

    /// <summary>从流读取 MP4/M4A 文件</summary>
    /// <param name="stream">输入流</param>
    public Mp4FileReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        ParseFile();
    }

    /// <summary>读取下一帧编码数据（AAC raw 或 ADTS）</summary>
    public IPacket ReadFrame()
    {
        if (_currentFrame >= _sampleCount) return null;

        var size = _sampleSizes[_currentFrame];
        var offset = _sampleOffsets[_currentFrame];
        _stream.Seek(offset, SeekOrigin.Begin);

        var buffer = new Byte[size];
        _stream.Read(buffer, 0, (Int32)size);
        _currentFrame++;

        return new ArrayPacket(buffer);
    }

    /// <summary>定位到指定帧</summary>
    /// <param name="frameIndex">帧索引</param>
    public void SeekFrame(Int64 frameIndex)
    {
        if (frameIndex < 0 || frameIndex >= _sampleCount) return;
        _currentFrame = frameIndex;
    }

    /// <summary>释放</summary>
    public void Dispose() => _stream?.Dispose();

    #region 解析
    private void ParseFile()
    {
        _format = AudioFormat.Default;
        _codecType = AVTypes.AAC;

        var stsdData = Array.Empty<Byte>();
        var sttsData = Array.Empty<Byte>();
        var stscData = Array.Empty<Byte>();
        var stszData = Array.Empty<Byte>();
        var stcoData = Array.Empty<Byte>();
        var stcoVersion = 0;
        var mdatFound = false;

        // 扫描全部 box：收集 stbl 数据 + 定位 mdat
        while (_stream.Position < _stream.Length - 8)
        {
            var boxStart = _stream.Position;
            var (boxType, boxData) = ReadBox();
            if (boxType == null) break;

            switch (boxType)
            {
                case "moov":
                    ParseMoov(boxData, ref stsdData, ref sttsData, ref stscData, ref stszData, ref stcoData, ref stcoVersion);
                    break;
                case "mdat":
                    mdatFound = true;
                    // 记录 mdat 数据起始（box header 之后）
                    // 不在此处 return，继续扫描（可能有 moov 在 mdat 之后）
                    break;
            }

            // 如果 mdat 在 moov 之前，继续扫描后续的 moov
        }

        // 所有 box 扫描完毕后再构建采样表
        BuildSampleTable(stsdData, sttsData, stscData, stszData, stcoData, stcoVersion);
    }

    private void ParseMoov(Byte[] moovData, ref Byte[] stsd, ref Byte[] stts, ref Byte[] stsc,
        ref Byte[] stsz, ref Byte[] stco, ref Int32 stcoVersion)
    {
        var reader = new SpanReader(moovData) { IsLittleEndian = false };
        while (reader.Available >= 8)
        {
            var size = reader.ReadUInt32();
            var type = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (size < 8 || reader.Position + (size - 8) > moovData.Length) break;

            var subSpan = reader.ReadBytes((Int32)size - 8);
            var r = new SpanReader(subSpan) { IsLittleEndian = false };

            switch (type)
            {
                case "mvhd":
                    ParseMvhd(ref r);
                    break;
                case "trak":
                    ParseTrak(ref r, ref stsd, ref stts, ref stsc, ref stsz, ref stco, ref stcoVersion);
                    break;
            }
        }
    }

    private void ParseTrak(ref SpanReader reader, ref Byte[] stsd, ref Byte[] stts, ref Byte[] stsc,
        ref Byte[] stsz, ref Byte[] stco, ref Int32 stcoVersion)
    {
        while (reader.Available >= 8)
        {
            var size = reader.ReadUInt32();
            var type = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (size < 8 || size - 8 > reader.Available) break;

            var subSpan = reader.ReadBytes((Int32)size - 8);

            switch (type)
            {
                case "tkhd":
                    break;
                case "mdia":
                    ParseMdia(subSpan, ref stsd, ref stts, ref stsc, ref stsz, ref stco, ref stcoVersion);
                    break;
            }
        }
    }

    private void ParseMdia(ReadOnlySpan<Byte> mdiaData, ref Byte[] stsd, ref Byte[] stts, ref Byte[] stsc,
        ref Byte[] stsz, ref Byte[] stco, ref Int32 stcoVersion)
    {
        var reader = new SpanReader(mdiaData) { IsLittleEndian = false };
        while (reader.Available >= 8)
        {
            var size = reader.ReadUInt32();
            var type = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (size < 8 || size - 8 > reader.Available) break;

            var subSpan = reader.ReadBytes((Int32)size - 8);
            var r = new SpanReader(subSpan) { IsLittleEndian = false };

            switch (type)
            {
                case "mdhd":
                    ParseMdhd(ref r);
                    break;
                case "minf":
                    ParseMinf(ref r, ref stsd, ref stts, ref stsc, ref stsz, ref stco, ref stcoVersion);
                    break;
            }
        }
    }

    private void ParseMinf(ref SpanReader reader, ref Byte[] stsd, ref Byte[] stts, ref Byte[] stsc,
        ref Byte[] stsz, ref Byte[] stco, ref Int32 stcoVersion)
    {
        while (reader.Available >= 8)
        {
            var size = reader.ReadUInt32();
            var type = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (size < 8 || size - 8 > reader.Available) break;

            var subSpan = reader.ReadBytes((Int32)size - 8);

            switch (type)
            {
                case "stbl":
                    ParseStbl(subSpan.ToArray(), ref stsd, ref stts, ref stsc, ref stsz, ref stco, ref stcoVersion);
                    break;
            }
        }
    }

    private void ParseStbl(Byte[] stblData, ref Byte[] stsd, ref Byte[] stts, ref Byte[] stsc,
        ref Byte[] stsz, ref Byte[] stco, ref Int32 stcoVersion)
    {
        var reader = new SpanReader(stblData) { IsLittleEndian = false };
        while (reader.Available >= 8)
        {
            var size = reader.ReadUInt32();
            var type = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (size < 8 || reader.Position + (size - 8) > stblData.Length) break;

            var subSpan = reader.ReadBytes((Int32)size - 8);

            switch (type)
            {
                case "stsd": stsd = subSpan.ToArray(); break;
                case "stts": stts = subSpan.ToArray(); break;
                case "stsc": stsc = subSpan.ToArray(); break;
                case "stsz": stsz = subSpan.ToArray(); break;
                case "stco": stco = subSpan.ToArray(); stcoVersion = 0; break;
                case "co64": stco = subSpan.ToArray(); stcoVersion = 1; break;
            }
        }
    }

    private void ParseMvhd(ref SpanReader reader)
    {
        if (reader.Available < 4) return;
        var version = reader.ReadByte();
        reader.Advance(3); // flags
        if (version == 1)
            reader.Advance(16); // 8B creation + 8B modification
        else
            reader.Advance(8);  // 4B creation + 4B modification
        if (reader.Available < 4) return;
        _timescale = reader.ReadInt32();
    }

    private void ParseMdhd(ref SpanReader reader)
    {
        if (reader.Available < 4) return;
        var version = reader.ReadByte();
        reader.Advance(3); // flags
        if (version == 1)
            reader.Advance(16); // 8B creation + 8B modification
        else
            reader.Advance(8);  // 4B creation + 4B modification
        if (reader.Available < 4) return;
        _timescale = reader.ReadInt32();
        // duration follows but we use stts for accurate frame counts
    }

    /// <summary>解析 stsd 中的 mp4a 描述信息</summary>
    private void ParseStsdAudio(Byte[] stsdData)
    {
        if (stsdData.Length < 16) return;
        var entryCount = stsdData[4] << 24 | stsdData[5] << 16 | stsdData[6] << 8 | stsdData[7];
        if (entryCount < 1) return;

        var offset = 8;
        if (offset + 8 > stsdData.Length) return;
        var descSize = stsdData[offset] << 24 | stsdData[offset + 1] << 16 | stsdData[offset + 2] << 8 | stsdData[offset + 3];
        var descType = Encoding.ASCII.GetString(stsdData, offset + 4, 4);
        if (descType != "mp4a" || descSize < 36) return;

        // mp4a box: 6B reserved, 2B dataRefIndex, 2B version, 2B revision, 4B vendor
        // 2B channels, 2B sampleSize(16), 2B preDefined, 2B reserved, 4B sampleRate
        var dataOff = offset + 8;
        var channels = (UInt16)((stsdData[dataOff + 16] << 8) | stsdData[dataOff + 17]);
        var sampleSize = (UInt16)((stsdData[dataOff + 18] << 8) | stsdData[dataOff + 19]);
        var sampleRate = (Int32)(((stsdData[dataOff + 24] << 24) | (stsdData[dataOff + 25] << 16) | (stsdData[dataOff + 26] << 8) | stsdData[dataOff + 27]) >> 16);

        _format = new AudioFormat
        {
            SampleRate = sampleRate > 0 ? sampleRate : 44100,
            Channels = channels > 0 ? channels : 2,
            BitsPerSample = sampleSize > 0 ? (Int32)sampleSize : 16,
            Encoding = AVTypes.AAC,
            SamplesPerFrame = 1024, // AAC-LC 典型帧长
        };

        _codecType = AVTypes.AAC;
    }

    /// <summary>构建采样偏移表</summary>
    private void BuildSampleTable(Byte[] stsd, Byte[] stts, Byte[] stsc, Byte[] stsz, Byte[] stco, Int32 stcoVersion)
    {
        ParseStsdAudio(stsd);

        // stsz: [version(1)+flags(3)] [sampleSize(4)] [entryCount(4)] + [entrySize(4)]*count
        if (stsz.Length < 12) return;
        var sampleSizeFixed = (UInt32)((stsz[4] << 24) | (stsz[5] << 16) | (stsz[6] << 8) | stsz[7]);
        var entryCount = stsz[8] << 24 | stsz[9] << 16 | stsz[10] << 8 | stsz[11];
        _sampleCount = entryCount;
        _sampleSizes = new UInt32[entryCount];

        if (sampleSizeFixed != 0)
        {
            for (var i = 0; i < entryCount; i++)
                _sampleSizes[i] = sampleSizeFixed;
        }
        else
        {
            var szOff = 12;
            for (var i = 0; i < entryCount && szOff + 4 <= stsz.Length; i++, szOff += 4)
                _sampleSizes[i] = (UInt32)((stsz[szOff] << 24) | (stsz[szOff + 1] << 16) | (stsz[szOff + 2] << 8) | stsz[szOff + 3]);
        }

        // stco/co64: [version+flags(4)] [entryCount(4)] + [offset(4 or 8)]*count
        if (stco.Length < 8) return;
        var chunkCount = stco[4] << 24 | stco[5] << 16 | stco[6] << 8 | stco[7];
        var chunkOffsets = new Int64[chunkCount];
        if (stcoVersion == 1)
        {
            var coOff = 8;
            for (var i = 0; i < chunkCount && coOff + 8 <= stco.Length; i++, coOff += 8)
                chunkOffsets[i] = (Int64)stco[coOff] << 56 | (Int64)stco[coOff + 1] << 48 |
                                   (Int64)stco[coOff + 2] << 40 | (Int64)stco[coOff + 3] << 32 |
                                   (Int64)stco[coOff + 4] << 24 | (Int64)stco[coOff + 5] << 16 |
                                   (Int64)stco[coOff + 6] << 8 | stco[coOff + 7];
        }
        else
        {
            var coOff = 8;
            for (var i = 0; i < chunkCount && coOff + 4 <= stco.Length; i++, coOff += 4)
                chunkOffsets[i] = (UInt32)((stco[coOff] << 24) | (stco[coOff + 1] << 16) | (stco[coOff + 2] << 8) | stco[coOff + 3]);
        }

        // stsc: [version+flags(4)] [entryCount(4)] + [firstChunk(4) samplesPerChunk(4) descIdx(4)]*count
        if (stsc.Length < 8) return;
        var stscEntryCount = stsc[4] << 24 | stsc[5] << 16 | stsc[6] << 8 | stsc[7];
        var stscEntries = new (Int32 firstChunk, Int32 samplesPerChunk, Int32 descIdx)[stscEntryCount];
        var scOff = 8;
        for (var i = 0; i < stscEntryCount && scOff + 12 <= stsc.Length; i++, scOff += 12)
        {
            stscEntries[i] = (
                stsc[scOff] << 24 | stsc[scOff + 1] << 16 | stsc[scOff + 2] << 8 | stsc[scOff + 3],
                stsc[scOff + 4] << 24 | stsc[scOff + 5] << 16 | stsc[scOff + 6] << 8 | stsc[scOff + 7],
                stsc[scOff + 8] << 24 | stsc[scOff + 9] << 16 | stsc[scOff + 10] << 8 | stsc[scOff + 11]
            );
        }

        // 构建每样本偏移
        _sampleOffsets = new Int64[entryCount];
        var sampleIdx = 0;
        var stscEntryIdx = 0;

        for (var chunk = 0; chunk < chunkCount && sampleIdx < entryCount; chunk++)
        {
            // 查找此 chunk 的 samplesPerChunk
            while (stscEntryIdx + 1 < stscEntryCount && stscEntries[stscEntryIdx + 1].firstChunk <= chunk + 1)
                stscEntryIdx++;

            var samplesPerChunk = stscEntries[stscEntryIdx].samplesPerChunk;
            var chunkOffset = chunkOffsets[chunk];

            for (var s = 0; s < samplesPerChunk && sampleIdx < entryCount; s++)
            {
                _sampleOffsets[sampleIdx] = chunkOffset;
                chunkOffset += _sampleSizes[sampleIdx];
                sampleIdx++;
            }
        }

        _totalFrames = entryCount;
    }
    #endregion

    #region Box 读取
    private (String type, Byte[] data) ReadBox()
    {
        var header = new Byte[8];
        if (_stream.Read(header, 0, 8) < 8) return (null, null);

        var size = (Int64)(UInt32)((header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3]);
        var type = Encoding.ASCII.GetString(header, 4, 4);

        Int64 dataSize;
        if (size == 1)
        {
            // 64-bit extended size
            var extSize = new Byte[8];
            _stream.Read(extSize, 0, 8);
            size = (Int64)extSize[0] << 56 | (Int64)extSize[1] << 48 |
                   (Int64)extSize[2] << 40 | (Int64)extSize[3] << 32 |
                   (Int64)extSize[4] << 24 | (Int64)extSize[5] << 16 |
                   (Int64)extSize[6] << 8 | extSize[7];
            dataSize = size - 16;
        }
        else if (size == 0)
        {
            // extends to end of file
            dataSize = _stream.Length - _stream.Position;
        }
        else
        {
            dataSize = size - 8;
        }

        if (dataSize <= 0) return (type, Array.Empty<Byte>());

        var data = new Byte[dataSize];
        _stream.Read(data, 0, (Int32)dataSize);
        return (type, data);
    }
    #endregion

}

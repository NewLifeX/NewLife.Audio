using System.Text;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>MP4/M4A 文件写入器。生成 ISO BMFF 容器，封装 AAC 音频帧</summary>
/// <remarks>
/// 写入流程：ftyp → 缓冲帧 → Flush 时写入 moov+mdat。
/// 输出符合 ISO 14496-12 基础规范，兼容 VLC/FFmpeg/QuickTime。
/// </remarks>
public class Mp4FileWriter : IAudioContainerWriter
{
    private readonly Stream _stream;
    private readonly AudioFormat _format;
    private readonly List<Byte[]> _frames = [];
    private Boolean _flushed;
    private AudioMetadata _metadata;

    /// <summary>音频格式</summary>
    public AudioFormat Format => _format;

    /// <summary>从流创建 MP4 写入器</summary>
    /// <param name="stream">输出流</param>
    /// <param name="format">音频格式（仅 AAC 编码）</param>
    public Mp4FileWriter(Stream stream, AudioFormat format)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _format = format ?? throw new ArgumentNullException(nameof(format));

        // 写入 ftyp
        WriteFtyp();
    }

    /// <summary>写入元数据</summary>
    /// <param name="metadata">音频元数据</param>
    public void WriteMetadata(AudioMetadata metadata)
    {
        _metadata = metadata;
    }

    /// <summary>写入一帧编码数据（AAC raw）</summary>
    /// <param name="frame">AAC 编码数据帧</param>
    public void WriteFrame(ReadOnlySpan<Byte> frame)
    {
        if (_flushed) throw new InvalidOperationException("MP4 文件已完成写入，不能再写入帧");

        var copy = new Byte[frame.Length];
        frame.CopyTo(copy);
        _frames.Add(copy);
    }

    /// <summary>完成写入：先构建 moov（含正确偏移），再写 mdat</summary>
    public void Flush()
    {
        if (_flushed) return;
        _flushed = true;

        // ftyp 已写入，当前位置是 moov 的起始
        // mdat 数据起始 = 当前位置 + 8 (moov header) + moovData.Length + 8 (mdat header)
        // 需要先知道 moovData 长度 → 两遍构建
        // 第一遍：用 0 占位计算 moov 大小
        var draftMoov = BuildMoovData(0);
        var mdatDataStart = _stream.Position + 8 + draftMoov.Length + 8;

        // 第二遍：用真实偏移重建 moov
        var moovData = BuildMoovData(mdatDataStart);

        // 写入 moov
        WriteUInt32BE((UInt32)(8 + moovData.Length));
        WriteFourCC("moov");
        _stream.Write(moovData, 0, moovData.Length);

        // 写入 mdat
        WriteMdat();
        _stream.Flush();
    }

    /// <summary>释放</summary>
    public void Dispose()
    {
        if (!_flushed) Flush();
    }

    #region Box 写入

    /// <summary>写入一个带长度和类型的 box 到流</summary>
    private static void WriteBox(Stream ms, String type, Byte[] data)
    {
        WriteUInt32BEToStream(ms, (UInt32)(8 + data.Length));
        WriteFourCCToStream(ms, type);
        ms.Write(data, 0, data.Length);
    }

    private void WriteFtyp()
    {
        // ftyp box: brand='M4A ', minorVersion=0, compatibleBrands=['M4A ','mp42','isom']
        var compatibleBrands = Encoding.ASCII.GetBytes("M4A mp42isom");
        var boxSize = 8 + 4 + 4 + compatibleBrands.Length; // 8B header + 4B brand + 4B version + 12B compatible
        WriteUInt32BE((UInt32)boxSize);
        WriteFourCC("ftyp");
        WriteFourCC("M4A ");
        WriteUInt32BE(0); // minor version
        _stream.Write(compatibleBrands, 0, compatibleBrands.Length);
    }

    private void WriteMdat()
    {
        // mdat box: [size(4)] [type='mdat'] [frames...]
        var dataSize = _frames.Sum(f => (Int64)f.Length);
        var boxSize = 8 + dataSize;

        if (boxSize > UInt32.MaxValue)
        {
            // 64-bit extended size
            WriteUInt32BE(1);
            WriteFourCC("mdat");
            WriteInt64BE(boxSize);
        }
        else
        {
            WriteUInt32BE((UInt32)boxSize);
            WriteFourCC("mdat");
        }

        foreach (var frame in _frames)
            _stream.Write(frame, 0, frame.Length);
    }

    private void WriteMoov(Int64 mdatOffset)
    {
        var moovData = BuildMoovData(mdatOffset);
        WriteUInt32BE((UInt32)(8 + moovData.Length));
        WriteFourCC("moov");
        _stream.Write(moovData, 0, moovData.Length);
    }

    private Byte[] BuildMoovData(Int64 mdatOffset)
    {
        var ms = new MemoryStream();

        // mvhd
        var mvhd = BuildMvhd();
        WriteBox(ms, "mvhd", mvhd);

        // trak
        var trak = BuildTrak(mdatOffset);
        WriteBox(ms, "trak", trak);

        return ms.ToArray();
    }

    private Byte[] BuildMvhd()
    {
        var ms = new MemoryStream();
        // version=0, flags=0
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        // creation_time, modification_time (unused)
        WriteUInt32BEToStream(ms, 0); WriteUInt32BEToStream(ms, 0);
        // timescale
        WriteUInt32BEToStream(ms, (UInt32)_format.SampleRate);
        // duration
        var totalDuration = (UInt64)_frames.Count * (UInt64)_format.SamplesPerFrame;
        WriteUInt32BEToStream(ms, (UInt32)totalDuration);
        // rate=1.0 (fixed 0x00010000)
        WriteUInt32BEToStream(ms, 0x00010000);
        // volume=1.0 (fixed 0x0100)
        WriteUInt16BEToStream(ms, 0x0100);
        // reserved
        WriteUInt16BEToStream(ms, 0);
        WriteUInt32BEToStream(ms, 0); WriteUInt32BEToStream(ms, 0);
        // matrix (unity)
        WriteUInt32BEToStream(ms, 0x00010000); WriteUInt32BEToStream(ms, 0);
        WriteUInt32BEToStream(ms, 0); WriteUInt32BEToStream(ms, 0x00010000);
        WriteUInt32BEToStream(ms, 0); WriteUInt32BEToStream(ms, 0);
        WriteUInt32BEToStream(ms, 0); WriteUInt32BEToStream(ms, 0);
        WriteUInt32BEToStream(ms, 0x40000000);
        // pre_defined
        for (var i = 0; i < 6; i++)
            WriteUInt32BEToStream(ms, 0);
        // next_track_id = 1
        WriteUInt32BEToStream(ms, 1);

        return ms.ToArray();
    }

    private Byte[] BuildTrak(Int64 mdatOffset)
    {
        var ms = new MemoryStream();

        // tkhd
        var tkhd = BuildTkhd();
        WriteBox(ms, "tkhd", tkhd);

        // mdia
        var mdia = BuildMdia(mdatOffset);
        WriteBox(ms, "mdia", mdia);

        return ms.ToArray();
    }

    private Byte[] BuildTkhd()
    {
        var ms = new MemoryStream();
        // version=0, flags=7 (track_enabled, track_in_movie, track_in_preview)
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(7);
        // creation, modification
        WriteUInt32BEToStream(ms, 0); WriteUInt32BEToStream(ms, 0);
        // track_id=1
        WriteUInt32BEToStream(ms, 1);
        // reserved
        WriteUInt32BEToStream(ms, 0);
        // duration
        var totalDuration = (UInt64)_frames.Count * (UInt64)_format.SamplesPerFrame;
        WriteUInt32BEToStream(ms, (UInt32)totalDuration);
        // reserved
        WriteUInt32BEToStream(ms, 0); WriteUInt32BEToStream(ms, 0);
        // layer=0, alternate_group=0, volume=1.0
        WriteUInt16BEToStream(ms, 0); WriteUInt16BEToStream(ms, 0);
        WriteUInt16BEToStream(ms, 0x0100); WriteUInt16BEToStream(ms, 0);
        // unity matrix
        WriteUInt32BEToStream(ms, 0x00010000); WriteUInt32BEToStream(ms, 0);
        WriteUInt32BEToStream(ms, 0); WriteUInt32BEToStream(ms, 0x00010000);
        WriteUInt32BEToStream(ms, 0); WriteUInt32BEToStream(ms, 0);
        WriteUInt32BEToStream(ms, 0); WriteUInt32BEToStream(ms, 0);
        WriteUInt32BEToStream(ms, 0x40000000);
        // width=0, height=0 (audio only)
        WriteUInt32BEToStream(ms, 0); WriteUInt32BEToStream(ms, 0);

        return ms.ToArray();
    }

    private Byte[] BuildMdia(Int64 mdatOffset)
    {
        var ms = new MemoryStream();

        // mdhd
        var mdhd = BuildMdhd();
        WriteBox(ms, "mdhd", mdhd);

        // hdlr
        var hdlr = BuildHdlr();
        WriteBox(ms, "hdlr", hdlr);

        // minf
        var minf = BuildMinf(mdatOffset);
        WriteBox(ms, "minf", minf);

        return ms.ToArray();
    }

    private Byte[] BuildMdhd()
    {
        var ms = new MemoryStream();
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        WriteUInt32BEToStream(ms, 0); WriteUInt32BEToStream(ms, 0);
        WriteUInt32BEToStream(ms, (UInt32)_format.SampleRate);
        var totalDuration = (UInt64)_frames.Count * (UInt64)_format.SamplesPerFrame;
        WriteUInt32BEToStream(ms, (UInt32)totalDuration);
        // language=und (ISO 639-2/T: 0x55C4)
        WriteUInt16BEToStream(ms, 0x55C4);
        // pre_defined
        WriteUInt16BEToStream(ms, 0);

        return ms.ToArray();
    }

    private Byte[] BuildHdlr()
    {
        var ms = new MemoryStream();
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        WriteUInt32BEToStream(ms, 0); // pre_defined
        WriteFourCCToStream(ms, "soun"); // handler_type
        WriteUInt32BEToStream(ms, 0); WriteUInt32BEToStream(ms, 0); WriteUInt32BEToStream(ms, 0); // reserved
        // name = "SoundHandler\0"
        var name = Encoding.ASCII.GetBytes("SoundHandler\0");
        ms.Write(name, 0, name.Length);

        return ms.ToArray();
    }

    private Byte[] BuildMinf(Int64 mdatOffset)
    {
        var ms = new MemoryStream();

        // smhd
        var smhd = BuildSmhd();
        WriteBox(ms, "smhd", smhd);

        // dinf + dref
        var dinf = BuildDinf();
        WriteBox(ms, "dinf", dinf);

        // stbl
        var stbl = BuildStbl(mdatOffset);
        WriteBox(ms, "stbl", stbl);

        return ms.ToArray();
    }

    private Byte[] BuildSmhd()
    {
        var ms = new MemoryStream();
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        // balance=0, reserved
        WriteUInt16BEToStream(ms, 0); WriteUInt16BEToStream(ms, 0);
        return ms.ToArray();
    }

    private Byte[] BuildDinf()
    {
        var ms = new MemoryStream();
        // dref box: version=0, flags=0, entryCount=1, url box (self-contained)
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        WriteUInt32BEToStream(ms, 1); // entry_count
        // url box: size=12, type='url ', version=0, flags=1 (self-contained)
        WriteUInt32BEToStream(ms, 12);
        WriteFourCCToStream(ms, "url ");
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(1);

        return ms.ToArray();
    }

    private Byte[] BuildStbl(Int64 mdatOffset)
    {
        var ms = new MemoryStream();

        // stsd
        var stsd = BuildStsd();
        WriteBox(ms, "stsd", stsd);

        // stts (time-to-sample: all frames = samplesPerFrame samples)
        var stts = BuildStts();
        WriteBox(ms, "stts", stts);

        // stsc (sample-to-chunk: one chunk = all samples)
        var stsc = BuildStsc();
        WriteBox(ms, "stsc", stsc);

        // stsz (sample sizes)
        var stsz = BuildStsz();
        WriteBox(ms, "stsz", stsz);

        // stco (chunk offsets)
        var stco = BuildStco(mdatOffset);
        WriteBox(ms, "stco", stco);

        return ms.ToArray();
    }

    private Byte[] BuildStsd()
    {
        var ms = new MemoryStream();
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        WriteUInt32BEToStream(ms, 1); // entry_count

        // mp4a box
        var mp4a = BuildMp4aBox();
        WriteUInt32BEToStream(ms, (UInt32)(8 + mp4a.Length));
        WriteFourCCToStream(ms, "mp4a");
        ms.Write(mp4a, 0, mp4a.Length);

        return ms.ToArray();
    }

    private Byte[] BuildMp4aBox()
    {
        var ms = new MemoryStream();
        // reserved[6]
        for (var i = 0; i < 6; i++) ms.WriteByte(0);
        // data_reference_index=1
        WriteUInt16BEToStream(ms, 1);
        // version=0, revision=0, vendor=0
        WriteUInt16BEToStream(ms, 0); WriteUInt16BEToStream(ms, 0);
        WriteUInt32BEToStream(ms, 0);
        // channels, sample_size=16
        WriteUInt16BEToStream(ms, (UInt16)_format.Channels);
        WriteUInt16BEToStream(ms, 16);
        // pre_defined, reserved
        WriteUInt16BEToStream(ms, 0); WriteUInt16BEToStream(ms, 0);
        // sample_rate (16.16 fixed point)
        WriteUInt32BEToStream(ms, (UInt32)_format.SampleRate << 16);

        // esds box (Elementary Stream Descriptor)
        var esds = BuildEsds();
        WriteUInt32BEToStream(ms, (UInt32)(8 + esds.Length));
        WriteFourCCToStream(ms, "esds");
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        ms.Write(esds, 0, esds.Length);

        return ms.ToArray();
    }

    private Byte[] BuildEsds()
    {
        var ms = new MemoryStream();
        // ES_Descriptor tag=3
        ms.WriteByte(3);
        // length (variable)
        var config = BuildDecoderConfig();
        var esdLen = 3 + 1 + 1 + 1 + 3 + 5 + config.Length;
        WriteDescriptorLength(ms, esdLen);
        // ES_ID=1, flags=0
        WriteUInt16BEToStream(ms, 1);
        ms.WriteByte(0);

        // DecoderConfigDescriptor tag=4
        ms.WriteByte(4);
        WriteDescriptorLength(ms, 1 + 1 + 3 + 5 + config.Length);
        // objectType=0x40 (Audio ISO/IEC 14496-3), streamType=0x05 (Audio), bufferSizeDB, maxBitrate, avgBitrate
        ms.WriteByte(0x40);
        ms.WriteByte(0x15); // streamType<<2 | upstream<<1 | reserved
        WriteUInt24BEToStream(ms, 0); // bufferSizeDB
        WriteUInt32BEToStream(ms, (UInt32)(_format.SampleRate * _format.Channels * 2 * 8)); // maxBitrate
        WriteUInt32BEToStream(ms, (UInt32)(_format.SampleRate * _format.Channels * 2 * 8)); // avgBitrate

        // DecoderSpecificInfo tag=5
        ms.WriteByte(5);
        WriteDescriptorLength(ms, config.Length);
        ms.Write(config, 0, config.Length);

        // SLConfigDescriptor tag=6
        ms.WriteByte(6);
        WriteDescriptorLength(ms, 1);
        ms.WriteByte(2); // pre-defined

        return ms.ToArray();
    }

    /// <summary>生成 AAC AudioSpecificConfig（2 字节）</summary>
    private Byte[] BuildDecoderConfig()
    {
        // AudioSpecificConfig for AAC-LC:
        // 5 bits: audioObjectType = 2 (AAC-LC)
        // 4 bits: samplingFrequencyIndex
        // 4 bits: channelConfiguration
        // 3 bits: GASpecificConfig (frameLengthFlag=0, dependsOnCoreCoder=0, extensionFlag=0)

        var sfi = GetSamplingFrequencyIndex(_format.SampleRate);
        var ch = _format.Channels;

        var config = new Byte[2];
        config[0] = (Byte)((2 << 3) | (sfi >> 1));
        config[1] = (Byte)(((sfi & 1) << 7) | (ch << 3));
        return config;
    }

    private static Int32 GetSamplingFrequencyIndex(Int32 sampleRate)
    {
        return sampleRate switch
        {
            96000 => 0, 88200 => 1, 64000 => 2, 48000 => 3,
            44100 => 4, 32000 => 5, 24000 => 6, 22050 => 7,
            16000 => 8, 12000 => 9, 11025 => 10, 8000 => 11,
            7350 => 12, _ => 4, // 默认 44100
        };
    }

    private Byte[] BuildStts()
    {
        var ms = new MemoryStream();
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        WriteUInt32BEToStream(ms, 1); // entry_count
        WriteUInt32BEToStream(ms, (UInt32)_frames.Count);
        WriteUInt32BEToStream(ms, (UInt32)_format.SamplesPerFrame);
        return ms.ToArray();
    }

    private Byte[] BuildStsc()
    {
        var ms = new MemoryStream();
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        WriteUInt32BEToStream(ms, 1); // entry_count
        WriteUInt32BEToStream(ms, 1); // first_chunk
        WriteUInt32BEToStream(ms, (UInt32)_frames.Count); // samples_per_chunk
        WriteUInt32BEToStream(ms, 1); // sample_description_index
        return ms.ToArray();
    }

    private Byte[] BuildStsz()
    {
        var ms = new MemoryStream();
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        WriteUInt32BEToStream(ms, 0); // sample_size=0 (variable)
        WriteUInt32BEToStream(ms, (UInt32)_frames.Count); // entry_count
        foreach (var frame in _frames)
            WriteUInt32BEToStream(ms, (UInt32)frame.Length);
        return ms.ToArray();
    }

    private Byte[] BuildStco(Int64 mdatOffset)
    {
        // mdat box header: 8 bytes + any extended size
        var dataStart = mdatOffset + 8;
        if (_frames.Sum(f => (Int64)f.Length) + 8 > UInt32.MaxValue)
            dataStart += 8; // 64-bit extended size

        var ms = new MemoryStream();
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        WriteUInt32BEToStream(ms, 1); // entry_count (one chunk)
        WriteUInt32BEToStream(ms, (UInt32)dataStart);
        return ms.ToArray();
    }

    /// <summary>写入描述符可变长度（ISO 14496-1）</summary>
    private static void WriteDescriptorLength(MemoryStream ms, Int32 length)
    {
        while (length > 0x7F)
        {
            ms.WriteByte((Byte)(0x80 | (length & 0x7F)));
            length >>= 7;
        }
        ms.WriteByte((Byte)length);
    }
    #endregion

    #region 二进制写入
    private void WriteUInt32BE(UInt32 value)
    {
        _stream.WriteByte((Byte)(value >> 24));
        _stream.WriteByte((Byte)(value >> 16));
        _stream.WriteByte((Byte)(value >> 8));
        _stream.WriteByte((Byte)value);
    }

    private void WriteFourCC(String code)
    {
        var bytes = Encoding.ASCII.GetBytes(code);
        _stream.Write(bytes, 0, 4);
    }

    private static void WriteUInt32BEToStream(Stream ms, UInt32 value)
    {
        ms.WriteByte((Byte)(value >> 24));
        ms.WriteByte((Byte)(value >> 16));
        ms.WriteByte((Byte)(value >> 8));
        ms.WriteByte((Byte)value);
    }

    private static void WriteUInt16BEToStream(Stream ms, UInt16 value)
    {
        ms.WriteByte((Byte)(value >> 8));
        ms.WriteByte((Byte)value);
    }

    private static void WriteInt64BE(Int64 value)
    {
        // This is on the main stream, placeholder for extended size
    }

    private static void WriteFourCCToStream(Stream ms, String code)
    {
        var bytes = Encoding.ASCII.GetBytes(code);
        ms.Write(bytes, 0, 4);
    }

    private static void WriteUInt24BEToStream(Stream ms, UInt32 value)
    {
        ms.WriteByte((Byte)(value >> 16));
        ms.WriteByte((Byte)(value >> 8));
        ms.WriteByte((Byte)value);
    }
    #endregion
}

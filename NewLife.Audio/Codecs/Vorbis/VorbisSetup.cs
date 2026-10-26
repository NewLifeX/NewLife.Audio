using System.Text;
using NewLife;

namespace NewLife.Audio.Codecs;

/// <summary>Vorbis 三个头包解析结果</summary>
internal sealed class VorbisSetup
{
    private VorbisSetup(Int32 channels, Int32 sampleRate, Int32 block0Size, Int32 block1Size, VorbisMode[] modes, Int32 modeFieldBits)
    {
        Channels = channels;
        SampleRate = sampleRate;
        Block0Size = block0Size;
        Block1Size = block1Size;
        Modes = modes;
        ModeFieldBits = modeFieldBits;
    }

    /// <summary>声道数</summary>
    public Int32 Channels { get; }

    /// <summary>采样率</summary>
    public Int32 SampleRate { get; }

    /// <summary>短块大小</summary>
    public Int32 Block0Size { get; }

    /// <summary>长块大小</summary>
    public Int32 Block1Size { get; }

    /// <summary>模式集合</summary>
    public VorbisMode[] Modes { get; }

    /// <summary>模式字段位数</summary>
    public Int32 ModeFieldBits { get; }

    /// <summary>读取三个 Vorbis 头包</summary>
    /// <param name="identification">标识头</param>
    /// <param name="comment">注释头</param>
    /// <param name="setup">设置头</param>
    /// <returns>解析结果</returns>
    public static VorbisSetup Read(ReadOnlySpan<Byte> identification, ReadOnlySpan<Byte> comment, ReadOnlySpan<Byte> setup)
    {
        var (channels, sampleRate, block0, block1) = ReadIdentification(identification);
        ReadComment(comment);

        var reader = new VorbisBitReader(setup);
        ReadSignature(ref reader, 5);

        var codebookCount = (Int32)reader.ReadBits(8) + 1;
        var codebooks = new VorbisCodebook[codebookCount];
        for (var i = 0; i < codebookCount; i++)
            codebooks[i] = VorbisCodebook.Read(ref reader);

        var timeCount = (Int32)reader.ReadBits(6) + 1;
        for (var i = 0; i < timeCount; i++)
        {
            if (reader.ReadBits(16) != 0) throw new InvalidDataException(L.T("The Vorbis time domain transform is invalid."));
        }

        var floorCount = (Int32)reader.ReadBits(6) + 1;
        var floors = new VorbisFloor[floorCount];
        for (var i = 0; i < floorCount; i++)
        {
            var type = (Int32)reader.ReadBits(16);
            floors[i] = type switch
            {
                0 => VorbisFloor0.Read(ref reader, codebooks, block0, block1),
                1 => VorbisFloor1.Read(ref reader, codebooks),
                _ => throw new InvalidDataException(L.T("The Vorbis floor type is invalid.")),
            };
        }

        var residueCount = (Int32)reader.ReadBits(6) + 1;
        var residues = new VorbisResidue[residueCount];
        for (var i = 0; i < residueCount; i++)
            residues[i] = VorbisResidue.Read(ref reader, channels, codebooks);

        var mappingCount = (Int32)reader.ReadBits(6) + 1;
        var mappings = new VorbisMapping[mappingCount];
        for (var i = 0; i < mappingCount; i++)
            mappings[i] = VorbisMapping.Read(ref reader, channels, floors, residues);

        var modeCount = (Int32)reader.ReadBits(6) + 1;
        var modes = new VorbisMode[modeCount];
        for (var i = 0; i < modeCount; i++)
            modes[i] = VorbisMode.Read(ref reader, block0, block1, mappings);

        if (reader.ReadBit() == 0) throw new InvalidDataException(L.T("The Vorbis setup header has no framing bit."));

        var modeFieldBits = VorbisCodebook.ILog(modeCount - 1);

        return new VorbisSetup(channels, sampleRate, block0, block1, modes, modeFieldBits);
    }

    private static (Int32 Channels, Int32 SampleRate, Int32 Block0, Int32 Block1) ReadIdentification(ReadOnlySpan<Byte> identification)
    {
        var reader = new VorbisBitReader(identification);
        ReadSignature(ref reader, 1);

        if (reader.ReadBits(32) != 0) throw new NotSupportedException(L.T("The Vorbis version is not supported."));

        var channels = (Int32)reader.ReadBits(8);
        var sampleRate = (Int32)reader.ReadBits(32);
        reader.SkipBits(32);
        reader.SkipBits(32);
        reader.SkipBits(32);

        var blockSizes = (Int32)reader.ReadBits(8);
        var block0 = 1 << (blockSizes & 0x0F);
        var block1 = 1 << ((blockSizes >> 4) & 0x0F);
        if (channels < 1 || sampleRate < 1) throw new InvalidDataException(L.T("The Vorbis identification header is invalid."));
        if (block0 < 64 || block1 < block0 || block1 > 8192) throw new InvalidDataException(L.T("The Vorbis block size is invalid."));
        if (reader.ReadBit() == 0) throw new InvalidDataException(L.T("The Vorbis identification header has no framing bit."));

        return (channels, sampleRate, block0, block1);
    }

    private static void ReadComment(ReadOnlySpan<Byte> comment)
    {
        var reader = new VorbisBitReader(comment);
        ReadSignature(ref reader, 3);

        var vendorLength = (Int32)reader.ReadBits(32);
        if (vendorLength < 0) throw new InvalidDataException(L.T("The Vorbis comment header is invalid."));
        SkipBytes(ref reader, vendorLength);

        var commentCount = (Int32)reader.ReadBits(32);
        if (commentCount < 0 || commentCount > 1 << 20) throw new InvalidDataException(L.T("The Vorbis comment header is invalid."));
        for (var i = 0; i < commentCount; i++)
        {
            var length = (Int32)reader.ReadBits(32);
            if (length < 0) throw new InvalidDataException(L.T("The Vorbis comment header is invalid."));
            SkipBytes(ref reader, length);
        }

        if (reader.ReadBit() == 0) throw new InvalidDataException(L.T("The Vorbis comment header has no framing bit."));
    }

    private static void SkipBytes(ref VorbisBitReader reader, Int32 length)
    {
        if (length < 0 || length > reader.BitsRemaining / 8) throw new InvalidDataException(L.T("The Vorbis header packet is too short."));
        reader.SkipBits(length * 8);
    }

    private static void ReadSignature(ref VorbisBitReader reader, Byte packetType)
    {
        if (reader.ReadBits(8) != packetType) throw new InvalidDataException(L.T("Not a Vorbis header packet."));

        if (reader.ReadByte() != (Byte)'v' || reader.ReadByte() != (Byte)'o' || reader.ReadByte() != (Byte)'r' ||
            reader.ReadByte() != (Byte)'b' || reader.ReadByte() != (Byte)'i' || reader.ReadByte() != (Byte)'s')
            throw new InvalidDataException(L.T("Not valid Vorbis data."));
    }
}

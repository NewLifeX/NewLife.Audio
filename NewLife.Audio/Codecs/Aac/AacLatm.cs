namespace NewLife.Audio.Codecs;

/// <summary>LATM/LOAS 读取器</summary>
/// <remarks>
/// 解析 ISO 14496-3 AudioMuxElement，输出 raw AAC access unit 与对应的 AudioSpecificConfig。
/// 支持单 program、单 layer、frameLengthType=0 的常见 LATM 流；每个 LOAS 帧可含多个 sub-frame。
/// </remarks>
internal sealed class AacLatmReader
{
    private Byte[] _asc;
    private Int32 _numSubFrames;
    private Boolean _otherDataPresent;
    private Int32 _otherDataLength;
    private Boolean _crcCheckPresent;

    /// <summary>读取完整 LOAS 数据中的全部 AAC access unit</summary>
    /// <param name="data">LOAS/LATM 数据</param>
    /// <returns>access unit 与 ASC 列表</returns>
    public IList<(Byte[] Frame, Byte[] Asc)> Read(ReadOnlySpan<Byte> data)
    {
        var frames = new List<(Byte[] Frame, Byte[] Asc)>();
        var offset = 0;

        while (offset + 3 <= data.Length)
        {
            // LOAS syncword：11 bits 0x2B7，随后 13 bits 长度
            if (data[offset] != 0x56 || (data[offset + 1] & 0xE0) != 0xE0)
            {
                offset++;
                continue;
            }

            var length = ((data[offset + 1] & 0x1F) << 8) | data[offset + 2];
            if (length <= 0 || offset + 3 + length > data.Length) break;

            var payload = data.Slice(offset + 3, length).ToArray();
            ParseAudioMuxElement(payload, frames);
            offset += 3 + length;
        }

        return frames;
    }

    private void ParseAudioMuxElement(Byte[] payload, IList<(Byte[] Frame, Byte[] Asc)> frames)
    {
        var reader = new AacBitReader(payload);
        var useSameStreamMux = reader.ReadBool();
        if (!useSameStreamMux) ReadStreamMuxConfig(reader);
        if (_asc == null) throw new InvalidDataException("LATM stream has no AudioSpecificConfig.");

        for (var i = 0; i <= _numSubFrames; i++)
        {
            var length = ReadPayloadLength(reader);
            frames.Add((reader.ReadBytes(length), _asc));
        }

        if (_otherDataPresent) reader.SkipBits(_otherDataLength * 8);
        if (_crcCheckPresent) reader.SkipBits(8);
    }

    private void ReadStreamMuxConfig(AacBitReader reader)
    {
        var audioMuxVersion = reader.ReadBool();
        if (audioMuxVersion)
        {
            var audioMuxVersionA = reader.ReadBool();
            if (audioMuxVersionA) throw new NotSupportedException("LATM audioMuxVersionA is not supported.");

            // taraBufferFullness
            ReadLatmValue(reader);
        }

        if (!reader.ReadBool()) throw new NotSupportedException("LATM allStreamsSameTimeFraming=0 is not supported.");

        _numSubFrames = (Int32)reader.ReadBits(6);
        var numProgram = (Int32)reader.ReadBits(4);
        var numLayer = (Int32)reader.ReadBits(3);
        if (numProgram != 0 || numLayer != 0) throw new NotSupportedException("LATM multi-program/multi-layer is not supported.");

        _asc = ReadAudioSpecificConfig(reader).ToByteArray();

        var frameLengthType = (Int32)reader.ReadBits(3);
        if (frameLengthType != 0) throw new NotSupportedException($"LATM frameLengthType {frameLengthType} is not supported.");

        reader.SkipBits(8); // latmBufferFullness

        _otherDataPresent = reader.ReadBool();
        if (_otherDataPresent) _otherDataLength = audioMuxVersion ? (Int32)ReadLatmValue(reader) : ReadPayloadLength(reader);

        _crcCheckPresent = reader.ReadBool();
        if (_crcCheckPresent) reader.SkipBits(8);
    }

    private static AudioSpecificConfig ReadAudioSpecificConfig(AacBitReader reader)
    {
        var aot = (Int32)reader.ReadBits(5);
        if (aot == 31) aot = 32 + (Int32)reader.ReadBits(6);

        var sfi = (Int32)reader.ReadBits(4);
        var sampleRate = sfi == 15 ? (Int32)reader.ReadBits(24) : AudioSpecificConfig.GetSampleRate(sfi);
        var channels = (Int32)reader.ReadBits(4);

        var asc = new AudioSpecificConfig
        {
            AudioObjectType = aot,
            SamplingFrequencyIndex = sfi,
            SamplingFrequency = sampleRate,
            ChannelConfiguration = channels,
        };

        if (aot == 5)
        {
            asc.ExtensionAudioObjectType = 5;
            asc.SbrPresentFlag = true;
            var extSfi = (Int32)reader.ReadBits(4);
            if (extSfi == 15)
            {
                asc.ExtensionSamplingFrequencyIndex = 15;
                asc.ExtensionSamplingFrequency = (Int32)reader.ReadBits(24);
            }
            else
            {
                asc.ExtensionSamplingFrequencyIndex = extSfi;
                asc.ExtensionSamplingFrequency = AudioSpecificConfig.GetSampleRate(extSfi);
            }
            aot = (Int32)reader.ReadBits(5);
            asc.AudioObjectType = aot;
        }

        switch (aot)
        {
            case 1:
            case 2:
            case 3:
            case 4:
            case 6:
            case 7:
            case 17:
            case 19:
            case 20:
            case 21:
            case 22:
            case 23:
                asc.FrameLengthFlag = reader.ReadBool();
                if (asc.FrameLengthFlag) throw new NotSupportedException("LATM 960-sample frames are not supported.");
                asc.DependsOnCoreCoder = reader.ReadBool();
                if (asc.DependsOnCoreCoder) asc.CoreCoderDelay = (Int32)reader.ReadBits(14);
                asc.ExtensionFlag = reader.ReadBool();
                if (asc.ExtensionFlag)
                {
                    if (aot is 17 or 19 or 20 or 21 or 22 or 23) reader.SkipBits(3);
                    reader.SkipBits(1);
                }
                if (channels == 0) throw new NotSupportedException("LATM PCE channel configuration is not supported.");
                break;
        }

        // 0x2B7 SBR 同步扩展（仅当后续位确实为 0x2B7 时消费；LATM 的 ASC 后面可能是 frameLengthType）
        if (reader.BitsLeft >= 11 && reader.PeekBits(11) == 0x2B7)
        {
            reader.SkipBits(11);
            if (reader.BitsLeft < 6) return asc;

            var extAot = (Int32)reader.ReadBits(5);
            if (extAot == 5)
            {
                if (reader.BitsLeft < 1) return asc;

                asc.ExtensionAudioObjectType = 5;
                asc.SbrPresentFlag = reader.ReadBool();
                if (asc.SbrPresentFlag)
                {
                    if (reader.BitsLeft < 4) return asc;

                    var extSfi = (Int32)reader.ReadBits(4);
                    if (extSfi == 15)
                    {
                        if (reader.BitsLeft < 24) return asc;

                        asc.ExtensionSamplingFrequencyIndex = 15;
                        asc.ExtensionSamplingFrequency = (Int32)reader.ReadBits(24);
                    }
                    else
                    {
                        asc.ExtensionSamplingFrequencyIndex = extSfi;
                        asc.ExtensionSamplingFrequency = AudioSpecificConfig.GetSampleRate(extSfi);
                    }

                    // PS：0x548 同步扩展，或部分编码器直接写 1 bit
                    if (reader.BitsLeft >= 12)
                    {
                        var first = reader.ReadBits(1);
                        if (first == 1)
                        {
                            var next = reader.ReadBits(10);
                            asc.PsPresentFlag = next == 0x148 ? reader.ReadBool() : true;
                        }
                    }
                    else if (reader.BitsLeft >= 1)
                        asc.PsPresentFlag = reader.ReadBool();
                }
            }
        }

        return asc;
    }

    private static UInt32 ReadLatmValue(AacBitReader reader)
    {
        var bytes = (Int32)reader.ReadBits(8);
        if (bytes > 4) throw new InvalidDataException("LATM latmGetValue length is too large.");

        UInt32 value = 0;
        for (var i = 0; i < bytes; i++) value = (value << 8) | reader.ReadBits(8);
        return value;
    }

    private static Int32 ReadPayloadLength(AacBitReader reader)
    {
        var length = 0;
        Int32 value;
        do
        {
            value = (Int32)reader.ReadBits(8);
            length += value;
        } while (value == 255);

        return length;
    }
}

/// <summary>LATM/LOAS 写入器</summary>
/// <remarks>
/// 生成单 program、单 layer、frameLengthType=0 的 LOAS 流；每个 AudioMuxElement 承载一个 raw AAC access unit。
/// 首帧携带 StreamMuxConfig，后续帧使用 useSameStreamMux=1。
/// </remarks>
public static class AacLatmWriter
{
    /// <summary>写入 LOAS 流</summary>
    /// <param name="asc">AudioSpecificConfig 字节</param>
    /// <param name="frames">raw AAC access unit 列表</param>
    /// <returns>LOAS 字节流</returns>
    public static Byte[] Write(Byte[] asc, IList<Byte[]> frames)
    {
        if (asc == null || asc.Length == 0) throw new ArgumentNullException(nameof(asc));
        if (frames == null || frames.Count == 0) return [];

        var output = new MemoryStream();
        for (var i = 0; i < frames.Count; i++)
        {
            var mux = new AacBitWriter();
            mux.WriteBits(i == 0 ? 0u : 1u, 1); // useSameStreamMux

            if (i == 0)
            {
                mux.WriteBits(0, 1); // audioMuxVersion
                mux.WriteBits(1, 1); // allStreamsSameTimeFraming
                mux.WriteBits(0, 6); // numSubFrames
                mux.WriteBits(0, 4); // numProgram
                mux.WriteBits(0, 3); // numLayer
                foreach (var b in asc) mux.WriteBits(b, 8);
                mux.WriteBits(0, 3);   // frameLengthType
                mux.WriteBits(255, 8); // latmBufferFullness
                mux.WriteBits(0, 1);   // otherDataPresent
                mux.WriteBits(0, 1);   // crcCheckPresent
            }

            WritePayloadLength(mux, frames[i].Length);
            foreach (var b in frames[i]) mux.WriteBits(b, 8);
            mux.ByteAlign();

            var payload = mux.ToArray();
            var length = payload.Length;
            output.WriteByte(0x56);
            output.WriteByte((Byte)(0xE0 | ((length >> 8) & 0x1F)));
            output.WriteByte((Byte)(length & 0xFF));
            output.Write(payload, 0, payload.Length);
        }

        return output.ToArray();
    }

    private static void WritePayloadLength(AacBitWriter writer, Int32 length)
    {
        while (length >= 255)
        {
            writer.WriteBits(255, 8);
            length -= 255;
        }
        writer.WriteBits((UInt32)length, 8);
    }
}

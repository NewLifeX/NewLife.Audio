using NewLife;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>Ogg Vorbis 编解码器</summary>
/// <remarks>
/// 纯托管 Vorbis I 实现：解码走 <see cref="VorbisDecoder"/>，编码走 <see cref="VorbisEncoder"/>，
/// 容器层使用 <see cref="OggFileReader"/> / <see cref="OggFileWriter"/>。
/// 输入输出均为完整 Ogg Vorbis 文件。
/// </remarks>
public class VorbisCodec : IAudioCodec, ICodecInfo, IDisposable
{
    /// <summary>编解码器名称</summary>
    public String Name => "Vorbis I";

    /// <summary>版本号</summary>
    public String Version => "1.0";

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = [AVTypes.Vorbis];

    /// <summary>有状态编解码器</summary>
    public Boolean IsStateful => true;

    /// <summary>Vorbis 数据转 PCM（输入为完整 Ogg Vorbis 文件）</summary>
    /// <param name="audio">Ogg Vorbis 文件数据</param>
    /// <param name="option">解码选项，可为 null</param>
    /// <returns>16-bit 小端交错 PCM</returns>
    public IPacket ToPcm(ReadOnlySpan<Byte> audio, Object option)
    {
        if (audio.Length < 4 || audio[0] != (Byte)'O' || audio[1] != (Byte)'g' || audio[2] != (Byte)'g' || audio[3] != (Byte)'S')
            throw new InvalidDataException(L.T("Not valid Ogg Vorbis data."));

        using var stream = new MemoryStream(audio.ToArray());
        using var reader = new OggFileReader(stream);
        if (reader.CodecType != AVTypes.Vorbis || reader.VorbisIdentification == null ||
            reader.VorbisComment == null || reader.VorbisSetup == null)
            throw new InvalidDataException(L.T("Not valid Ogg Vorbis data."));

        var decoder = new VorbisDecoder(reader.VorbisIdentification, reader.VorbisComment, reader.VorbisSetup);
        var channels = decoder.Channels;
        var total = reader.TotalSamples;
        var expected = total > 0 && total <= Int32.MaxValue / channels ? (Int32)total * channels : 0;
        if (expected <= 0) throw new InvalidDataException(L.T("The Ogg Vorbis stream has no audio samples."));

        var output = new Single[expected];
        var written = 0;
        var buffer = new Single[decoder.MaxPacketSamples * channels];

        while (true)
        {
            var packet = reader.ReadFrame();
            if (packet == null) break;

            var count = decoder.DecodePacket(packet.GetSpan(), buffer);
            written = CopySamples(buffer, count * channels, output, written, expected);
        }

        var flush = decoder.Flush(buffer);
        written = CopySamples(buffer, flush * channels, output, written, expected);

        if (written > expected) written = expected;

        var pcm = new Byte[written * 2];
        for (var i = 0; i < written; i++)
        {
            var value = output[i] * 32767f;
            var sample = value <= -32768f ? Int16.MinValue : value >= 32767f ? Int16.MaxValue : (Int16)Math.Round(value);
            pcm[i * 2] = (Byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (Byte)((sample >> 8) & 0xFF);
        }

        return new ArrayPacket(pcm);
    }

    /// <summary>PCM 转 Vorbis（输出为完整 Ogg Vorbis 文件）</summary>
    /// <param name="pcm">16-bit 小端交错 PCM</param>
    /// <param name="option"><see cref="VorbisOptions"/>、质量档位 Int32 或 null</param>
    /// <returns>Ogg Vorbis 文件数据</returns>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Object option)
    {
        var options = option as VorbisOptions ?? new VorbisOptions();
        if (option is Int32 quality) options.Quality = quality;
        options.Validate();

        using var encoder = new VorbisEncoder(options);
        var headers = encoder.Headers;

        using var stream = new MemoryStream();
        using (var writer = new OggFileWriter(stream, new AudioFormat
        {
            SampleRate = options.SampleRate,
            Channels = options.Channels,
            BitsPerSample = 16,
            Encoding = AVTypes.Vorbis,
        }, AVTypes.Vorbis))
        {
            writer.WriteVorbisHeaders(headers[0], headers[1], headers[2]);

            var channels = options.Channels;
            var samplesPerChannel = pcm.Length / 2 / channels;
            var frameSamples = options.BlockSize0;
            var floatSamples = new Single[frameSamples * channels];

            for (var offset = 0; offset < samplesPerChannel; offset += frameSamples)
            {
                var count = Math.Min(frameSamples, samplesPerChannel - offset);
                for (var i = 0; i < count * channels; i++)
                {
                    var index = (offset * channels + i) * 2;
                    var sample = (Int16)(pcm[index] | pcm[index + 1] << 8);
                    floatSamples[i] = sample / 32768f;
                }

                var packet = encoder.EncodePacket(floatSamples.AsSpan(0, count * channels), count);
                if (packet.Length > 0)
                    writer.WriteFrame(packet, encoder.LastPacketSamples);
            }

            while (true)
            {
                var tail = encoder.Flush();
                if (tail.Length == 0) break;
                writer.WriteFrame(tail, encoder.LastPacketSamples);
            }
        }

        return new ArrayPacket(stream.ToArray());
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
    }

    private static Int32 CopySamples(Single[] source, Int32 count, Single[] destination, Int32 offset, Int32 limit)
    {
        if (count <= 0) return offset;

        var available = limit - offset;
        if (available <= 0) return offset;

        var length = Math.Min(count, available);
        Array.Copy(source, 0, destination, offset, length);

        return offset + length;
    }
}

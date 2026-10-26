using System;
using System.IO;
using NewLife.Audio;
using NewLife.Audio.Containers;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest;

/// <summary>AIFF/CAF 容器测试：写入读取往返、探测识别与 CAF 解析</summary>
public class AiffCafTests
{
    private static readonly AudioFormat Format = new() { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };

    [Fact(DisplayName = "AIFF 写入后探测识别为 AIFF")]
    public void Aiff_ProbeDetects()
    {
        var path = TempPath(".aiff");
        try
        {
            AudioFile.WritePcm(path, Format, BuildSine(0.1));

            var info = AudioFile.Detect(path);
            Assert.Equal(AudioContainerKind.Aiff, info.Container);
            Assert.Equal(16000, info.Format.SampleRate);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "AIFF 往返数据一致")]
    public void Aiff_RoundTrip()
    {
        var pcm = BuildSine(0.1);
        var path = TempPath(".aiff");
        try
        {
            AudioFile.WritePcm(path, Format, pcm);

            var audio = AudioFile.ReadPcm(path);
            Assert.Equal(16000, audio.Format.SampleRate);
            Assert.Equal(1, audio.Format.Channels);
            Assert.Equal(pcm.Length, audio.Data.Length);
            Assert.Equal(pcm, audio.Data);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "AIFF 立体声往返保持声道顺序")]
    public void Aiff_Stereo_RoundTrip()
    {
        var format = new AudioFormat { SampleRate = 44100, Channels = 2, BitsPerSample = 16 };
        var pcm = new Byte[200 * 2 * 2];
        for (var i = 0; i < 200; i++)
        {
            var l = (Int16)(1000 + i);
            var r = (Int16)(-1000 - i);
            pcm[i * 4] = (Byte)(l & 0xFF);
            pcm[i * 4 + 1] = (Byte)((l >> 8) & 0xFF);
            pcm[i * 4 + 2] = (Byte)(r & 0xFF);
            pcm[i * 4 + 3] = (Byte)((r >> 8) & 0xFF);
        }

        var path = TempPath(".aiff");
        try
        {
            AudioFile.WritePcm(path, format, pcm);

            var audio = AudioFile.ReadPcm(path);
            Assert.Equal(2, audio.Format.Channels);
            Assert.Equal(pcm, audio.Data);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "CAF 读取未压缩 16 位 PCM")]
    public void Caf_ReadPcm()
    {
        var pcm = BuildSine(0.05);
        var path = TempPath(".caf");
        try
        {
            File.WriteAllBytes(path, BuildCaf(pcm, Format.SampleRate, Format.Channels, bigEndian: false));

            var info = AudioFile.Detect(path);
            Assert.Equal(AudioContainerKind.Caf, info.Container);

            var audio = AudioFile.ReadPcm(path);
            Assert.Equal(Format.SampleRate, audio.Format.SampleRate);
            Assert.Equal(pcm.Length, audio.Data.Length);
            Assert.Equal(pcm, audio.Data);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact(DisplayName = "CAF 大端 16 位 PCM 正确解码")]
    public void Caf_BigEndian_ReadPcm()
    {
        var pcm = BuildSine(0.02);
        var path = TempPath(".caf");
        try
        {
            File.WriteAllBytes(path, BuildCaf(pcm, Format.SampleRate, Format.Channels, bigEndian: true));

            var audio = AudioFile.ReadPcm(path);
            Assert.Equal(pcm.Length, audio.Data.Length);
            Assert.Equal(pcm, audio.Data);
        }
        finally
        {
            TryDelete(path);
        }
    }

    #region 辅助
    private static String TempPath(String extension) => Path.Combine(Path.GetTempPath(), "nlaudio_" + Guid.NewGuid().ToString("N") + extension);

    private static void TryDelete(String path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    private static Byte[] BuildSine(Double seconds)
    {
        var samples = (Int32)(Format.SampleRate * seconds);
        var data = new Byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (Int16)(Math.Sin(2 * Math.PI * 440 * i / Format.SampleRate) * 0.6 * 32000);
            data[i * 2] = (Byte)(v & 0xFF);
            data[i * 2 + 1] = (Byte)((v >> 8) & 0xFF);
        }

        return data;
    }

    /// <summary>构造最小合法 CAF：caff + desc(lpcm 16 位) + data</summary>
    private static Byte[] BuildCaf(Byte[] pcm16, Int32 sampleRate, Int32 channels, Boolean bigEndian)
    {
        using var ms = new MemoryStream();
        var writer = new BinaryWriter(ms);

        writer.Write(new[] { (Byte)'c', (Byte)'a', (Byte)'f', (Byte)'f' });
        writer.Write((Int16)1);
        writer.Write((Int16)0);

        // desc
        writer.Write(new[] { (Byte)'d', (Byte)'e', (Byte)'s', (Byte)'c' });
        WriteBE64(writer, 32);
        WriteBE64(writer, BitConverter.DoubleToInt64Bits(sampleRate));
        writer.Write(new[] { (Byte)'l', (Byte)'p', (Byte)'c', (Byte)'m' });
        var flags = bigEndian ? 0x02 : 0x00;
        WriteBE32(writer, flags);
        WriteBE32(writer, 2 * channels);   // bytesPerPacket
        WriteBE32(writer, 1);              // framesPerPacket
        WriteBE32(writer, channels);
        WriteBE32(writer, 16);

        // data
        writer.Write(new[] { (Byte)'d', (Byte)'a', (Byte)'t', (Byte)'a' });
        WriteBE64(writer, pcm16.Length + 4);
        WriteBE32(writer, 0);              // editCount

        if (bigEndian)
        {
            var swapped = new Byte[pcm16.Length];
            for (var i = 0; i < pcm16.Length; i += 2)
            {
                swapped[i] = pcm16[i + 1];
                swapped[i + 1] = pcm16[i];
            }
            writer.Write(swapped);
        }
        else
        {
            writer.Write(pcm16);
        }

        writer.Flush();
        return ms.ToArray();
    }

    private static void WriteBE32(BinaryWriter writer, Int32 value)
    {
        writer.Write((Byte)(value >> 24));
        writer.Write((Byte)(value >> 16));
        writer.Write((Byte)(value >> 8));
        writer.Write((Byte)value);
    }

    private static void WriteBE64(BinaryWriter writer, Int64 value)
    {
        for (var i = 7; i >= 0; i--) writer.Write((Byte)(value >> (8 * i)));
    }
    #endregion
}

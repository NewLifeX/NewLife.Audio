using NewLife.Data;

namespace NewLife.Audio.Codecs;

public class G711UCodec : IAudioCodec
{
    /* 16384 entries per table (16 bit) */
    private readonly Byte[] linearToUlawTable = new Byte[65536];

    /* 16384 entries per table (8 bit) */
    private readonly Int16[] ulawToLinearTable = new Int16[256];
    private readonly Int32 SIGN_BIT = 0x80;
    private readonly Int32 QUANT_MASK = 0x0f;
    private readonly Int32 SEG_SHIFT = 0x04;
    private readonly Int32 SEG_MASK = 0x70;
    private readonly Int32 BIAS = 0x84;
    private readonly Int32 CLIP = 8159;
    private readonly Int16[] seg_uend = { 0x3F, 0x7F, 0xFF, 0x1FF, 0x3FF, 0x7FF, 0xFFF, 0x1FFF };

    public G711UCodec()
    {
        // 初始化ulaw表
        for (var i = 0; i < 256; i++) ulawToLinearTable[i] = Ulaw2linear((Byte)i);
        // 初始化ulaw2linear表
        for (var i = 0; i < 65535; i++) linearToUlawTable[i] = Linear2ulaw((Int16)i);
    }

    private Int16 Ulaw2linear(Byte ulawValue)
    {
        ulawValue = (Byte)~ulawValue;
        var t = (Int16)(((ulawValue & QUANT_MASK) << 3) + BIAS);
        t <<= (ulawValue & SEG_MASK) >> SEG_SHIFT;

        return (ulawValue & SIGN_BIT) > 0 ? (Int16)(BIAS - t) : (Int16)(t - BIAS);
    }

    private Byte Linear2ulaw(Int16 pcmValue)
    {
        Int16 mask;
        Int16 seg;
        Byte uval;

        pcmValue = (Int16)(pcmValue >> 2);
        if (pcmValue < 0)
        {
            pcmValue = (Int16)(-pcmValue);
            mask = 0x7f;
        }
        else
            mask = 0xff;

        if (pcmValue > CLIP) pcmValue = (Int16)CLIP;
        pcmValue += (Int16)(BIAS >> 2);

        seg = Search(pcmValue, seg_uend, 8);

        if (seg >= 8)
            return (Byte)(0x7f ^ mask);
        else
        {
            uval = (Byte)(seg << 4 | pcmValue >> seg + 1 & 0xF);
            return (Byte)(uval ^ mask);
        }
    }

    private Int16 Search(Int16 val, Int16[] table, Int16 size)
    {
        for (Int16 i = 0; i < size; i++)
            if (val <= table[i]) return i;
        return size;
    }

    private Byte[] UlawToPcm16(Byte[] samples)
    {
        var pcmSamples = new Byte[samples.Length * 2];
        for (Int32 i = 0, k = 0; i < samples.Length; i++)
        {
            var s = ulawToLinearTable[samples[i] & 0xff];
            pcmSamples[k++] = (Byte)(s & 0xff);
            pcmSamples[k++] = (Byte)(s >> 8 & 0xff);
        }
        return pcmSamples;
    }

    //private byte[] Pcm16ToUlaw(byte[] pcmSamples)
    //{
    //    short[] dst = new short[pcmSamples.Length / 2];
    //    byte[] ulawSamples = new byte[pcmSamples.Length / 2];
    //    for (int i = 0, k = 0; i < pcmSamples.Length;)
    //    {
    //        dst[k++] = (short)((pcmSamples[i++] & 0xff) | ((pcmSamples[i++] & 0xff) << 8));
    //    }
    //    for (int i = 0, k = 0; i < dst.Length; i++)
    //    {
    //        ulawSamples[k++] = Linear2ulaw(dst[i]);
    //    }
    //    return ulawSamples;
    //}

    //public byte[] ToG711(byte[] data) => Pcm16ToUlaw(data);

    /// <summary>音频数据转PCM</summary>
    /// <param name="audio"></param>
    /// <param name="option"></param>
    /// <returns></returns>
    public Packet ToPcm(Packet audio, Object option) => UlawToPcm16(audio.ReadBytes());

    /// <summary>PCM转音频数据</summary>
    /// <param name="pcm"></param>
    /// <param name="option"></param>
    /// <returns></returns>
    public Packet FromPcm(Packet pcm, Object option) => throw new NotImplementedException();
}

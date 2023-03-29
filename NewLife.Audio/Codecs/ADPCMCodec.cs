using System.Text;
using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>ADPCM差分脉冲编码调制</summary>
/// <remarks>
/// 主要是针对连续的波形数据的, 保存的是相临波形的变化情况, 以达到描述整个波形的目的。
/// 本文的以IMA的ADPCM编码标准为例进行描述，IMA-ADPCM 是Intel公司首先开发的是一种主要针对16bit采样波形数据的有损压缩算法，压缩比为 4:1。
/// 它与通常的DVI-ADPCM是同一算法。
/// </remarks>
public class ADPCMCodec : IAudioCodec
{
    /* Intel ADPCM step variation table */
    private static readonly Int32[] indexTable = {
        -1, -1, -1, -1, 2, 4, 6, 8,
        -1, -1, -1, -1, 2, 4, 6, 8
    };
    private static readonly Int32[] stepsizeTable = {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
        19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
        337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
        876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
        2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
        5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
    };

    private void adpcm_coder(Int16[] indata, Stream outp, AdpcmState state)
    {
        Int32 val;            /* Current input sample value */
        Int32 sign;           /* Current adpcm sign bit */
        Int32 delta;          /* Current adpcm output value */
        Int32 diff;           /* Difference between val and valprev */
        Int32 step;           /* Stepsize */
        Int32 valpred;        /* Predicted output value */
        Int32 vpdiff;         /* Current change to valpred */
        Int32 index;          /* Current step change index */
        var outputbuffer = 0;       /* place to keep previous 4-bit value */
        Int32 bufferstep;     /* toggle between outputbuffer/output */

        //var outp = new MemoryStream();
        var inp = indata;
        var len = indata.Length;
        valpred = state.Valprev;
        index = state.Index;
        step = stepsizeTable[index];

        bufferstep = 1;
        for (var i = 0; len > 0; len--, i++)
        {
            val = inp[i];

            /* Step 1 - compute difference with previous value */
            diff = val - valpred;
            sign = diff < 0 ? 8 : 0;
            if (sign != 0) diff = -diff;

            /* Step 2 - Divide and clamp */
            /* Note:
            ** This code *approximately* computes:
            **    delta = diff*4/step;
            **    vpdiff = (delta+0.5)*step/4;
            ** but in shift step bits are dropped. The net result of this is
            ** that even if you have fast mul/div hardware you cannot put it to
            ** good use since the fixup would be too expensive.
            */
            delta = 0;
            vpdiff = step >> 3;

            if (diff >= step)
            {
                delta = 4;
                diff -= step;
                vpdiff += step;
            }
            step >>= 1;
            if (diff >= step)
            {
                delta |= 2;
                diff -= step;
                vpdiff += step;
            }
            step >>= 1;
            if (diff >= step)
            {
                delta |= 1;
                vpdiff += step;
            }

            /* Step 3 - Update previous value */
            if (sign != 0)
                valpred -= vpdiff;
            else
                valpred += vpdiff;

            /* Step 4 - Clamp previous value to 16 bits */
            if (valpred > 32767)
                valpred = 32767;
            else if (valpred < -32768)
                valpred = -32768;

            /* Step 5 - Assemble value, update index and step values */
            delta |= sign;

            index += indexTable[delta];
            if (index < 0) index = 0;
            if (index > 88) index = 88;
            step = stepsizeTable[index];

            /* Step 6 - Output value */
            if (bufferstep != 0)
                outputbuffer = delta << 4 & 0xf0;
            else
                outp.WriteByte((Byte)(delta & 0x0f | outputbuffer));

            ////adpcm_ima
            //if (bufferstep != 0)
            //{
            //    outputbuffer = delta & 0x0f;
            //}
            //else
            //{
            //    outp.WriteByte((Byte)(((delta << 4) & 0xf0) | outputbuffer));
            //}
            bufferstep = bufferstep == 0 ? 1 : 0;
        }

        /* Output last step, if needed */
        if (bufferstep == 0)
            outp.WriteByte((Byte)outputbuffer);

        state.Valprev = (Int16)valpred;
        state.Index = (Byte)index;

        //return outp.ToArray();
    }

    /// <summary>
    /// 将adpcm转为pcm
    /// </summary>
    /// <see cref="https://github.com/ctuning/ctuning-programs/blob/master/program/cbench-telecom-adpcm-d/adpcm.c"/>
    /// <param name="audio"></param>
    /// <param name="outdata"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    private void adpcm_decoder(Byte[] audio, Stream outdata, AdpcmState state)
    {
        // signed char *inp;		/* Input buffer pointer */
        // short *outp;		/* output buffer pointer */
        Int32 sign;           /* Current adpcm sign bit */
        Int32 delta;          /* Current adpcm output value */
        Int32 step;           /* Stepsize */
        Int32 valpred;        /* Predicted value */
        Int32 vpdiff;         /* Current change to valpred */
        Int32 index;          /* Current step change index */
        var inputbuffer = 0;        /* place to keep next 4-bit value */
        var bufferstep = false;     /* toggle between inputbuffer/input */

        valpred = state.Valprev;
        index = state.Index;
        if (index < 0) index = 0;
        if (index > 88) index = 88;
        step = stepsizeTable[index];

        //var outdata = new MemoryStream();
        var len = audio.Length * 2;
        for (var i = 0; len > 0; len--)
        {
            /* Step 1 - get the delta value */
            if (bufferstep)
                delta = inputbuffer & 0xf;
            else
            {
                inputbuffer = audio[i++];
                delta = inputbuffer >> 4 & 0xf;
            }
            bufferstep = !bufferstep;

            /* Step 2 - Find new index value (for later) */
            index += indexTable[delta];
            if (index < 0) index = 0;
            if (index > 88) index = 88;

            /* Step 3 - Separate sign and magnitude */
            sign = delta & 8;
            delta &= 7;

            /* Step 4 - Compute difference and new predicted value */
            /*
            ** Computes 'vpdiff = (delta+0.5)*step/4', but see comment
            ** in adpcm_coder.
            */
            vpdiff = step >> 3;
            if ((delta & 4) > 0) vpdiff += step;
            if ((delta & 2) > 0) vpdiff += step >> 1;
            if ((delta & 1) > 0) vpdiff += step >> 2;

            if (sign != 0)
                valpred -= vpdiff;
            else
                valpred += vpdiff;

            /* Step 5 - clamp output value */
            if (valpred > 32767)
                valpred = 32767;
            else if (valpred < -32768)
                valpred = -32768;

            /* Step 6 - Update step value */
            step = stepsizeTable[index];

            /* Step 7 - Output value */
            //outdata.AddRange(BitConverter.GetBytes((Int16)valpred));
            outdata.WriteByte((Byte)(valpred & 0xff));
            outdata.WriteByte((Byte)(valpred >> 8));
        }
        state.Valprev = (Int16)valpred;
        state.Index = (Byte)index;

        //return outdata.ToArray();
    }

    /// <summary>PCM转音频数据</summary>
    /// <param name="pcm"></param>
    /// <param name="option"></param>
    /// <returns></returns>
    public Packet FromPcm(Packet pcm, Object option)
    {
        var state = option as AdpcmState;
        state ??= new AdpcmState();

        var pcmdata = new Int16[pcm.Total / 2];
        for (var i = 0; i < pcmdata.Length; i++)
            pcmdata[i] = (Int16)(pcm[i * 2 + 1] << 8 | pcm[i * 2]);

        var ms = new MemoryStream();
        ms.WriteByte((Byte)(state.Valprev & 0xFF));
        ms.WriteByte((Byte)(state.Valprev >> 8));
        ms.WriteByte(state.Index);
        ms.WriteByte(state.Reserved);

        adpcm_coder(pcmdata, ms, state);

        return ms.ToArray();
    }

    /// <summary>音频数据转PCM</summary>
    /// <param name="audio"></param>
    /// <param name="option"></param>
    /// <returns></returns>
    public Packet ToPcm(Packet audio, Object option)
    {
        if (option is not AdpcmState state)
        {
            state = new AdpcmState()
            {
                Valprev = (Int16)(audio[1] << 8 | audio[0]),
                Index = audio[2],
                Reserved = audio[3]
            };
            audio = audio[4..];
        }

        var ms = new MemoryStream();

        adpcm_decoder(audio.ReadBytes(), ms, state);

        return ms.ToArray();
    }
}

class AdpcmState
{
    /// <summary>
    /// 上一个采样数据，当index为0是该值应该为未压缩的原数据
    /// </summary>
    public Int16 Valprev { get; set; }

    /// <summary>
    /// 保留数据（未使用）
    /// </summary>
    public Byte Reserved { get; set; }

    /// <summary>
    /// 上一个block最后一个index，第一个block的index=0
    /// </summary>
    public Byte Index { get; set; }
}

static class AdpcmDecoderExtension
{
    /// <summary>
    /// 添加wav头
    /// 仅用于测试pcm是否转成成功，因此没考虑性能，因为播放器可播——#
    /// </summary>
    /// <param name="input">pcm数据</param>
    /// <param name="frequency">采样率</param>
    /// <param name="bitDepth">位深</param>
    /// <returns></returns>
    public static Byte[] ToWav(this Byte[] input, UInt32 frequency, Byte bitDepth = 16)
    {
        var output = new Byte[input.Length + 44];
        Array.Copy(Encoding.ASCII.GetBytes("RIFF"), 0, output, 0, 4);
        WriteUint(4, (UInt32)output.Length - 8, output);
        Array.Copy(Encoding.ASCII.GetBytes("WAVE"), 0, output, 8, 4);
        Array.Copy(Encoding.ASCII.GetBytes("fmt "), 0, output, 12, 4);
        WriteUint(16, 16, output); //Header size
        output[20] = 1; //PCM
        output[22] = 1; //1 channel
        WriteUint(24, frequency, output); //Sample Rate
        WriteUint(28, (UInt32)(frequency * (bitDepth / 8)), output); //Bytes per second
        output[32] = (Byte)(bitDepth >> 3); //Bytes per sample
        output[34] = bitDepth; //Bits per sample
        Array.Copy(Encoding.ASCII.GetBytes("data"), 0, output, 36, 4);
        WriteUint(40, (UInt32)output.Length, output); //Date size
        Array.Copy(input, 0, output, 44, input.Length);
        return output;
    }

    private static void WriteUint(UInt32 offset, UInt32 value, Byte[] destination)
    {
        for (var i = 0; i < 4; i++)
        {
            destination[offset + i] = (Byte)(value & 0xFF);
            value >>= 8;
        }
    }
}
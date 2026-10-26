using System;

namespace NewLife.Audio.Codecs;

/// <summary>MP3（MPEG Layer III）解码的 DSP 内核</summary>
/// <remarks>
/// 混合滤波器组由「IMDCT + 窗口叠加」与「多相综合滤波」两段组成。
/// 叠代窗口、旋转因子与综合窗均为 ISO/IEC 11172-3 附录中的定值，
/// 快速算法与公开领域实现（minimp3, CC0）一致，避免使用矩阵直接相乘的高开销写法。
/// </remarks>
internal static class Mp3Layer3Dsp
{
    /// <summary>综合滤波器组的组合窗，15 组 × 16 个系数</summary>
    private static readonly Single[] SynthWindow =
    [
        -1.0f, 26.0f, -31.0f, 208.0f, 218.0f, 401.0f, -519.0f, 2063.0f, 2000.0f, 4788.0f, -5517.0f, 7134.0f,
        5959.0f, 35640.0f, -39336.0f, 74992.0f, -1.0f, 24.0f, -35.0f, 202.0f, 222.0f, 347.0f, -581.0f, 2080.0f,
        1952.0f, 4425.0f, -5879.0f, 7640.0f, 5288.0f, 33791.0f, -41176.0f, 74856.0f, -1.0f, 21.0f, -38.0f, 196.0f,
        225.0f, 294.0f, -645.0f, 2087.0f, 1893.0f, 4063.0f, -6237.0f, 8092.0f, 4561.0f, 31947.0f, -43006.0f, 74630.0f,
        -1.0f, 19.0f, -41.0f, 190.0f, 227.0f, 244.0f, -711.0f, 2085.0f, 1822.0f, 3705.0f, -6589.0f, 8492.0f,
        3776.0f, 30112.0f, -44821.0f, 74313.0f, -1.0f, 17.0f, -45.0f, 183.0f, 228.0f, 197.0f, -779.0f, 2075.0f,
        1739.0f, 3351.0f, -6935.0f, 8840.0f, 2935.0f, 28289.0f, -46617.0f, 73908.0f, -1.0f, 16.0f, -49.0f, 176.0f,
        228.0f, 153.0f, -848.0f, 2057.0f, 1644.0f, 3004.0f, -7271.0f, 9139.0f, 2037.0f, 26482.0f, -48390.0f, 73415.0f,
        -2.0f, 14.0f, -53.0f, 169.0f, 227.0f, 111.0f, -919.0f, 2032.0f, 1535.0f, 2663.0f, -7597.0f, 9389.0f,
        1082.0f, 24694.0f, -50137.0f, 72835.0f, -2.0f, 13.0f, -58.0f, 161.0f, 224.0f, 72.0f, -991.0f, 2001.0f,
        1414.0f, 2330.0f, -7910.0f, 9592.0f, 70.0f, 22929.0f, -51853.0f, 72169.0f, -2.0f, 11.0f, -63.0f, 154.0f,
        221.0f, 36.0f, -1064.0f, 1962.0f, 1280.0f, 2006.0f, -8209.0f, 9750.0f, -998.0f, 21189.0f, -53534.0f, 71420.0f,
        -2.0f, 10.0f, -68.0f, 147.0f, 215.0f, 2.0f, -1137.0f, 1919.0f, 1131.0f, 1692.0f, -8491.0f, 9863.0f,
        -2122.0f, 19478.0f, -55178.0f, 70590.0f, -3.0f, 9.0f, -73.0f, 139.0f, 208.0f, -29.0f, -1210.0f, 1870.0f,
        970.0f, 1388.0f, -8755.0f, 9935.0f, -3300.0f, 17799.0f, -56778.0f, 69679.0f, -3.0f, 8.0f, -79.0f, 132.0f,
        200.0f, -57.0f, -1283.0f, 1817.0f, 794.0f, 1095.0f, -8998.0f, 9966.0f, -4533.0f, 16155.0f, -58333.0f, 68692.0f,
        -4.0f, 7.0f, -85.0f, 125.0f, 189.0f, -83.0f, -1356.0f, 1759.0f, 605.0f, 814.0f, -9219.0f, 9959.0f,
        -5818.0f, 14548.0f, -59838.0f, 67629.0f, -4.0f, 7.0f, -91.0f, 117.0f, 177.0f, -106.0f, -1428.0f, 1698.0f,
        402.0f, 545.0f, -9416.0f, 9916.0f, -7154.0f, 12980.0f, -61289.0f, 66494.0f, -5.0f, 6.0f, -97.0f, 111.0f,
        163.0f, -127.0f, -1498.0f, 1634.0f, 185.0f, 288.0f, -9585.0f, 9838.0f, -8540.0f, 11455.0f, -62684.0f, 65290.0f,
    ];

    /// <summary>DCT-II 旋转系数，8 组 × 3</summary>
    private static readonly Single[] Sec =
    [
        10.19000816f, 0.50060302f, 0.50241929f, 3.40760851f, 0.50547093f, 0.52249861f,
        2.05778098f, 0.51544732f, 0.56694406f, 1.48416460f, 0.53104258f, 0.64682180f,
        1.16943991f, 0.55310392f, 0.78815460f, 0.97256821f, 0.58293498f, 1.06067765f,
        0.83934963f, 0.62250412f, 1.72244716f, 0.74453628f, 0.67480832f, 5.10114861f,
    ];

    /// <summary>长块 IMDCT 的旋转系数</summary>
    private static readonly Single[] Twiddle9 =
    [
        0.73727734f, 0.79335334f, 0.84339145f, 0.88701083f, 0.92387953f, 0.95371695f,
        0.97629601f, 0.99144486f, 0.99904822f, 0.67559021f, 0.60876143f, 0.53729961f,
        0.46174861f, 0.38268343f, 0.30070580f, 0.21643961f, 0.13052619f, 0.04361938f,
    ];

    /// <summary>短块 IMDCT 的旋转系数</summary>
    private static readonly Single[] Twiddle3 =
    [
        0.79335334f, 0.92387953f, 0.99144486f, 0.60876143f, 0.38268343f, 0.13052619f,
    ];

    /// <summary>反混叠蝶形的余弦系数</summary>
    private static readonly Single[] AntiAliasCs =
    [
        0.85749293f, 0.88174200f, 0.94962865f, 0.98331459f, 0.99551782f, 0.99916056f, 0.99989920f, 0.99999316f,
    ];

    /// <summary>反混叠蝶形的正弦系数</summary>
    private static readonly Single[] AntiAliasCa =
    [
        0.51449576f, 0.47173197f, 0.31337745f, 0.18191320f, 0.09457419f, 0.04096558f, 0.01419856f, 0.00369997f,
    ];

    /// <summary>长块与启动/停止块的 IMDCT 窗，18 个系数</summary>
    private static readonly Single[] LongWindow =
    [
        0.99904822f, 0.99144486f, 0.97629601f, 0.95371695f, 0.92387953f, 0.88701083f,
        0.84339145f, 0.79335334f, 0.73727734f, 0.04361938f, 0.13052619f, 0.21643961f,
        0.30070580f, 0.38268343f, 0.46174861f, 0.53729961f, 0.60876143f, 0.67559021f,
    ];

    /// <summary>启动/停止块的 IMDCT 窗，18 个系数</summary>
    private static readonly Single[] EdgeWindow =
    [
        1f, 1f, 1f, 1f, 1f, 1f,
        0.99144486f, 0.92387953f, 0.79335334f, 0f, 0f, 0f,
        0f, 0f, 0f, 0.13052619f, 0.38268343f, 0.60876143f,
    ];

    /// <summary>频谱重排序，把三窗口交错的短块频谱整理成窗口优先</summary>
    /// <param name="spectrum">谱线缓冲</param>
    /// <param name="offset">起始谱线偏移（混合块跳过前面的长块部分）</param>
    /// <param name="scratch">至少 576 个元素的临时缓冲</param>
    /// <param name="bands">缩放因子带宽度，短块按「带 ×3 窗口」展开</param>
    /// <param name="bandOffset">起始带号</param>
    /// <returns>实际写出的谱线数</returns>
    public static Int32 Reorder(Single[] spectrum, Int32 offset, Single[] scratch, Byte[] bands, Int32 bandOffset)
    {
        var src = offset;
        var dst = 0;
        for (var b = bandOffset; b + 2 < bands.Length; b += 3)
        {
            var len = bands[b];
            if (len == 0) break;
            for (var i = 0; i < len; i++)
            {
                scratch[dst++] = spectrum[src + i];
                scratch[dst++] = spectrum[src + i + len];
                scratch[dst++] = spectrum[src + i + 2 * len];
            }
            src += 3 * len;
        }

        Array.Copy(scratch, 0, spectrum, offset, dst);
        return dst;
    }

    /// <summary>反混叠蝶形，作用在相邻子带的交界处</summary>
    /// <param name="spectrum">谱线缓冲</param>
    /// <param name="offset">起始谱线偏移</param>
    /// <param name="bands">需要处理的子带数</param>
    public static void AntiAlias(Single[] spectrum, Int32 offset, Int32 bands)
    {
        for (; bands > 0; bands--, offset += 18)
        {
            for (var i = 0; i < 8; i++)
            {
                var u = spectrum[offset + 18 + i];
                var d = spectrum[offset + 17 - i];
                spectrum[offset + 18 + i] = u * AntiAliasCs[i] - d * AntiAliasCa[i];
                spectrum[offset + 17 - i] = u * AntiAliasCa[i] + d * AntiAliasCs[i];
            }
        }
    }

    /// <summary>奇数子带的样本符号翻转（频率反转）</summary>
    /// <param name="spectrum">时域样本缓冲</param>
    /// <param name="offset">起始偏移</param>
    public static void ChangeSign(Single[] spectrum, Int32 offset)
    {
        for (var b = 0; b < 32; b += 2)
        {
            var start = offset + (b + 1) * 18;
            for (var i = 1; i < 18; i += 2)
            {
                spectrum[start + i] = -spectrum[start + i];
            }
        }
    }

    /// <summary>IMDCT 与窗口叠加</summary>
    /// <param name="spectrum">谱线缓冲，原地变为时域样本</param>
    /// <param name="offset">起始谱线偏移</param>
    /// <param name="overlap">每声道 32×9 的重叠缓冲</param>
    /// <param name="overlapOffset">重叠缓冲起始偏移</param>
    /// <param name="blockType">块类型，2 为短块，3 为停止块</param>
    /// <param name="longBands">前部按长块处理的子带数</param>
    public static void Imdct(Single[] spectrum, Int32 offset, Single[] overlap, Int32 overlapOffset, Int32 blockType, Int32 longBands)
    {
        var grOffset = offset;
        var ovOffset = overlapOffset;
        if (longBands > 0)
        {
            ImdctLong(spectrum, grOffset, overlap, ovOffset, LongWindow, longBands);
            grOffset += 18 * longBands;
            ovOffset += 9 * longBands;
        }

        var rest = 32 - longBands;
        if (blockType == 2)
            ImdctShort(spectrum, grOffset, overlap, ovOffset, rest);
        else
            ImdctLong(spectrum, grOffset, overlap, ovOffset, blockType == 3 ? EdgeWindow : LongWindow, rest);
    }

    private static void ImdctLong(Single[] grbuf, Int32 grOffset, Single[] overlap, Int32 ovOffset, Single[] window, Int32 bands)
    {
        for (var j = 0; j < bands; j++, grOffset += 18, ovOffset += 9)
        {
            var co = new Single[9];
            var si = new Single[9];
            co[0] = -grbuf[grOffset];
            si[0] = grbuf[grOffset + 17];
            for (var i = 0; i < 4; i++)
            {
                si[8 - 2 * i] = grbuf[grOffset + 4 * i + 1] - grbuf[grOffset + 4 * i + 2];
                co[1 + 2 * i] = grbuf[grOffset + 4 * i + 1] + grbuf[grOffset + 4 * i + 2];
                si[7 - 2 * i] = grbuf[grOffset + 4 * i + 4] - grbuf[grOffset + 4 * i + 3];
                co[2 + 2 * i] = -(grbuf[grOffset + 4 * i + 3] + grbuf[grOffset + 4 * i + 4]);
            }

            Dct3_9(co);
            Dct3_9(si);

            si[1] = -si[1];
            si[3] = -si[3];
            si[5] = -si[5];
            si[7] = -si[7];

            for (var i = 0; i < 9; i++)
            {
                var ovl = overlap[ovOffset + i];
                var sum = co[i] * Twiddle9[9 + i] + si[i] * Twiddle9[i];
                overlap[ovOffset + i] = co[i] * Twiddle9[i] - si[i] * Twiddle9[9 + i];
                grbuf[grOffset + i] = ovl * window[i] - sum * window[9 + i];
                grbuf[grOffset + 17 - i] = ovl * window[9 + i] + sum * window[i];
            }
        }
    }

    private static void ImdctShort(Single[] grbuf, Int32 grOffset, Single[] overlap, Int32 ovOffset, Int32 bands)
    {
        var tmp = new Single[18];
        for (; bands > 0; bands--, ovOffset += 9, grOffset += 18)
        {
            Array.Copy(grbuf, grOffset, tmp, 0, 18);
            Array.Copy(overlap, ovOffset, grbuf, grOffset, 6);
            Imdct12(tmp, 0, grbuf, grOffset + 6, overlap, ovOffset + 6);
            Imdct12(tmp, 1, grbuf, grOffset + 12, overlap, ovOffset + 6);
            Imdct12(tmp, 2, overlap, ovOffset, overlap, ovOffset + 6);
        }
    }

    private static void Imdct12(Single[] x, Int32 xOffset, Single[] dst, Int32 dstOffset, Single[] overlap, Int32 ovOffset)
    {
        var co = new Single[3];
        var si = new Single[3];

        Idct3(-x[xOffset], x[xOffset + 6] + x[xOffset + 3], x[xOffset + 12] + x[xOffset + 9], co);
        Idct3(x[xOffset + 15], x[xOffset + 12] - x[xOffset + 9], x[xOffset + 6] - x[xOffset + 3], si);
        si[1] = -si[1];

        for (var i = 0; i < 3; i++)
        {
            var ovl = overlap[ovOffset + i];
            var sum = co[i] * Twiddle3[3 + i] + si[i] * Twiddle3[i];
            overlap[ovOffset + i] = co[i] * Twiddle3[i] - si[i] * Twiddle3[3 + i];
            dst[dstOffset + i] = ovl * Twiddle3[2 - i] - sum * Twiddle3[5 - i];
            dst[dstOffset + 5 - i] = ovl * Twiddle3[5 - i] + sum * Twiddle3[2 - i];
        }
    }

    private static void Idct3(Single x0, Single x1, Single x2, Single[] dst)
    {
        var m1 = x1 * 0.86602540f;
        var a1 = x0 - x2 * 0.5f;
        dst[1] = x0 + x2;
        dst[0] = a1 + m1;
        dst[2] = a1 - m1;
    }

    private static void Dct3_9(Single[] y)
    {
        var s0 = y[0]; var s2 = y[2]; var s4 = y[4]; var s6 = y[6]; var s8 = y[8];
        var t0 = s0 + s6 * 0.5f;
        s0 -= s6;
        var t4 = (s4 + s2) * 0.93969262f;
        var t2 = (s8 + s2) * 0.76604444f;
        s6 = (s4 - s8) * 0.17364818f;
        s4 += s8 - s2;

        s2 = s0 - s4 * 0.5f;
        y[4] = s4 + s0;
        s8 = t0 - t2 + s6;
        s0 = t0 - t4 + t2;
        s4 = t0 + t4 - s6;

        var s1 = y[1]; var s3 = y[3]; var s5 = y[5]; var s7 = y[7];

        s3 *= 0.86602540f;
        t0 = (s5 + s1) * 0.98480775f;
        t4 = (s5 - s7) * 0.34202014f;
        t2 = (s1 + s7) * 0.64278761f;
        s1 = (s1 - s5 - s7) * 0.86602540f;

        s5 = t0 - s3 - t2;
        s7 = t4 - s3 - t0;
        s3 = t4 + s3 - t2;

        y[0] = s4 - s7;
        y[1] = s2 + s1;
        y[2] = s0 - s3;
        y[3] = s8 + s5;
        y[5] = s8 - s5;
        y[6] = s0 + s3;
        y[7] = s2 - s1;
        y[8] = s4 + s7;
    }

    /// <summary>32 点 DCT-II，把 18 个时隙的子带样本变换到综合滤波输入</summary>
    /// <param name="grbuf">时域样本缓冲</param>
    /// <param name="offset">声道起始偏移</param>
    private static void DctII(Single[] grbuf, Int32 offset)
    {
        var t = new Single[4, 8];
        for (var k = 0; k < 18; k++)
        {
            var y = offset + k;
            for (var i = 0; i < 8; i++)
            {
                var x0 = grbuf[y + i * 18];
                var x1 = grbuf[y + (15 - i) * 18];
                var x2 = grbuf[y + (16 + i) * 18];
                var x3 = grbuf[y + (31 - i) * 18];
                var t0 = x0 + x3;
                var t1 = x1 + x2;
                var t2 = (x1 - x2) * Sec[3 * i];
                var t3 = (x0 - x3) * Sec[3 * i + 1];
                t[0, i] = t0 + t1;
                t[1, i] = (t0 - t1) * Sec[3 * i + 2];
                t[2, i] = t3 + t2;
                t[3, i] = (t3 - t2) * Sec[3 * i + 2];
            }

            for (var r = 0; r < 4; r++)
            {
                var x0 = t[r, 0]; var x1 = t[r, 1]; var x2 = t[r, 2]; var x3 = t[r, 3];
                var x4 = t[r, 4]; var x5 = t[r, 5]; var x6 = t[r, 6]; var x7 = t[r, 7];
                var xt = x0 - x7; x0 += x7;
                x7 = x1 - x6; x1 += x6;
                x6 = x2 - x5; x2 += x5;
                x5 = x3 - x4; x3 += x4;
                x4 = x0 - x3; x0 += x3;
                x3 = x1 - x2; x1 += x2;
                t[r, 0] = x0 + x1;
                t[r, 4] = (x0 - x1) * 0.70710677f;
                x5 += x6;
                x6 = (x6 + x7) * 0.70710677f;
                x7 += xt;
                x3 = (x3 + x4) * 0.70710677f;
                x5 -= x7 * 0.198912367f;
                x7 += x5 * 0.382683432f;
                x5 -= x7 * 0.198912367f;
                x0 = xt - x6; xt += x6;
                t[r, 1] = (xt + x7) * 0.50979561f;
                t[r, 2] = (x4 + x3) * 0.54119611f;
                t[r, 3] = (x0 - x5) * 0.60134488f;
                t[r, 5] = (x0 + x5) * 0.89997619f;
                t[r, 6] = (x4 - x3) * 1.30656302f;
                t[r, 7] = (xt - x7) * 2.56291556f;
            }

            for (var i = 0; i < 7; i++)
            {
                grbuf[offset + (4 * i + 0) * 18 + k] = t[0, i];
                grbuf[offset + (4 * i + 1) * 18 + k] = t[2, i] + t[3, i] + t[3, i + 1];
                grbuf[offset + (4 * i + 2) * 18 + k] = t[1, i] + t[1, i + 1];
                grbuf[offset + (4 * i + 3) * 18 + k] = t[2, i + 1] + t[3, i] + t[3, i + 1];
            }
            grbuf[offset + (28 + 0) * 18 + k] = t[0, 7];
            grbuf[offset + (28 + 1) * 18 + k] = t[2, 7] + t[3, 7];
            grbuf[offset + (28 + 2) * 18 + k] = t[1, 7];
            grbuf[offset + (28 + 3) * 18 + k] = t[3, 7];
        }
    }

    /// <summary>多相综合滤波，把 576 个子带样本还原为 PCM</summary>
    /// <param name="qmf">每声道 15×64 的滤波器状态（交错存放左右声道）</param>
    /// <param name="grbuf">576 个子带样本</param>
    /// <param name="channels">声道数</param>
    /// <param name="pcm">PCM 输出，交错存放</param>
    /// <param name="pcmOffset">输出起始下标</param>
    /// <param name="lins">至少 15×64+18×64 的临时缓冲</param>

    public static void SynthGranule(Single[] qmf, Single[] grbuf, Int32 channels, Int16[] pcm, Int32 pcmOffset, Single[] lins)
    {
        for (var ch = 0; ch < channels; ch++)
        {
            DctII(grbuf, ch * 576);
        }

        Array.Copy(qmf, 0, lins, 0, 15 * 64);

        for (var i = 0; i < 18; i += 2)
        {
            Synth(grbuf, i, pcm, pcmOffset + 32 * channels * i, channels, lins, i * 64);
        }

        if (channels == 1)
        {
            for (var i = 0; i < 15 * 64; i += 2)
            {
                qmf[i] = lins[18 * 64 + i];
            }
        }
        else
        {
            Array.Copy(lins, 18 * 64, qmf, 0, 15 * 64);
        }
    }

    private static void Synth(Single[] x, Int32 xOffset, Int16[] pcm, Int32 pcmOffset, Int32 channels, Single[] lins, Int32 linOffset)
    {
        var zlin = linOffset + 15 * 64;
        var xr = xOffset + 576 * (channels - 1);

        lins[zlin + 4 * 15] = x[xOffset + 18 * 16];
        lins[zlin + 4 * 15 + 1] = x[xr + 18 * 16];
        lins[zlin + 4 * 15 + 2] = x[xOffset];
        lins[zlin + 4 * 15 + 3] = x[xr];

        lins[zlin + 4 * 31] = x[xOffset + 1 + 18 * 16];
        lins[zlin + 4 * 31 + 1] = x[xr + 1 + 18 * 16];
        lins[zlin + 4 * 31 + 2] = x[xOffset + 1];
        lins[zlin + 4 * 31 + 3] = x[xr + 1];

        SynthPair(pcm, pcmOffset + channels - 1, channels, lins, linOffset + 4 * 15 + 1);
        SynthPair(pcm, pcmOffset + 32 * channels + channels - 1, channels, lins, linOffset + 4 * 15 + 64 + 1);
        SynthPair(pcm, pcmOffset, channels, lins, linOffset + 4 * 15);
        SynthPair(pcm, pcmOffset + 32 * channels, channels, lins, linOffset + 4 * 15 + 64);

        var w = 0;
        for (var i = 14; i >= 0; i--)
        {
            lins[zlin + 4 * i] = x[xOffset + 18 * (31 - i)];
            lins[zlin + 4 * i + 1] = x[xr + 18 * (31 - i)];
            lins[zlin + 4 * i + 2] = x[xOffset + 1 + 18 * (31 - i)];
            lins[zlin + 4 * i + 3] = x[xr + 1 + 18 * (31 - i)];
            lins[zlin + 4 * (i + 16)] = x[xOffset + 1 + 18 * (1 + i)];
            lins[zlin + 4 * (i + 16) + 1] = x[xr + 1 + 18 * (1 + i)];
            lins[zlin + 4 * (i - 16) + 2] = x[xOffset + 18 * (1 + i)];
            lins[zlin + 4 * (i - 16) + 3] = x[xr + 18 * (1 + i)];

            var a = new Single[4];
            var b = new Single[4];
            for (var k = 0; k < 8; k++)
            {
                var w0 = SynthWindow[w++];
                var w1 = SynthWindow[w++];
                var vz = zlin + 4 * i - k * 64;
                var vy = zlin + 4 * i - (15 - k) * 64;
                for (var j = 0; j < 4; j++)
                {
                    if (k == 0)
                    {
                        b[j] = lins[vz + j] * w1 + lins[vy + j] * w0;
                        a[j] = lins[vz + j] * w0 - lins[vy + j] * w1;
                    }
                    else
                    {
                        b[j] += lins[vz + j] * w1 + lins[vy + j] * w0;
                        if (k % 2 == 1)
                            a[j] += lins[vy + j] * w1 - lins[vz + j] * w0;
                        else
                            a[j] += lins[vz + j] * w0 - lins[vy + j] * w1;
                    }
                }
            }


            pcm[pcmOffset + (15 - i) * channels + channels - 1] = ScalePcm(a[1]);
            pcm[pcmOffset + (17 + i) * channels + channels - 1] = ScalePcm(b[1]);
            pcm[pcmOffset + (47 - i) * channels + channels - 1] = ScalePcm(a[3]);
            pcm[pcmOffset + (49 + i) * channels + channels - 1] = ScalePcm(b[3]);
            pcm[pcmOffset + (15 - i) * channels] = ScalePcm(a[0]);
            pcm[pcmOffset + (17 + i) * channels] = ScalePcm(b[0]);
            pcm[pcmOffset + (47 - i) * channels] = ScalePcm(a[2]);
            pcm[pcmOffset + (49 + i) * channels] = ScalePcm(b[2]);
        }
    }

    private static void SynthPair(Int16[] pcm, Int32 pcmOffset, Int32 channels, Single[] lins, Int32 z)
    {
        var a = (lins[z + 14 * 64] - lins[z]) * 29;
        a += (lins[z + 1 * 64] + lins[z + 13 * 64]) * 213;
        a += (lins[z + 12 * 64] - lins[z + 2 * 64]) * 459;
        a += (lins[z + 3 * 64] + lins[z + 11 * 64]) * 2037;
        a += (lins[z + 10 * 64] - lins[z + 4 * 64]) * 5153;
        a += (lins[z + 5 * 64] + lins[z + 9 * 64]) * 6574;
        a += (lins[z + 8 * 64] - lins[z + 6 * 64]) * 37489;
        a += lins[z + 7 * 64] * 75038;
        pcm[pcmOffset] = ScalePcm(a);

        z += 2;
        a = lins[z + 14 * 64] * 104;
        a += lins[z + 12 * 64] * 1567;
        a += lins[z + 10 * 64] * 9727;
        a += lins[z + 8 * 64] * 64019;
        a += lins[z + 6 * 64] * -9975;
        a += lins[z + 4 * 64] * -45;
        a += lins[z + 2 * 64] * 146;
        a += lins[z + 0 * 64] * -5;
        pcm[pcmOffset + 16 * channels] = ScalePcm(a);
    }

    private static Int16 ScalePcm(Single sample)
    {
        if (sample >= 32766.5f) return 32767;
        if (sample <= -32767.5f) return -32768;

        var s = (Int16)(sample + 0.5f);
        if (s < 0) s--;

        return s;
    }
}






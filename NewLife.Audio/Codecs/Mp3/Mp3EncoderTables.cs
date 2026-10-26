using System;

namespace NewLife.Audio.Codecs;

/// <summary>MP3 编码器所需的常数表</summary>
/// <remarks>
/// 分析滤波窗由解码器的综合窗（ISO/IEC 11172-3 原型滤波器）反序扩展得到，
/// 保证分析/综合构成完美重构对；MDCT 窗、旋转因子与反混叠系数均按标准公式在静态构造时生成，
/// 避免硬编码大体积浮点常量。
/// </remarks>
internal static class Mp3EncoderTables
{
    #region 常量
    /// <summary>子带数</summary>
    public const Int32 Subbands = 32;

    /// <summary>多相分析的输入块长度</summary>
    public const Int32 FilterLength = 512;

    /// <summary>长块 MDCT 输入长度</summary>
    public const Int32 BlockLength = 36;

    /// <summary>单子带 MDCT 系数个数</summary>
    public const Int32 MdctLines = 18;

    /// <summary>反混叠蝶形的余弦系数</summary>
    public static readonly Single[] AliasCs =
    [
        0.85749293f, 0.88174200f, 0.94962865f, 0.98331459f, 0.99551782f, 0.99916056f, 0.99989920f, 0.99999316f,
    ];

    /// <summary>反混叠蝶形的正弦系数</summary>
    public static readonly Single[] AliasCa =
    [
        0.51449576f, 0.47173197f, 0.31337745f, 0.18191320f, 0.09457419f, 0.04096558f, 0.01419856f, 0.00369997f,
    ];
    #endregion

    #region 字段
    private static readonly Single[] _analysisWindow = BuildAnalysisWindow();
    private static readonly Single[] _matrix = BuildMatrix();
    private static readonly Single[] _mdctWindow = BuildMdctWindow();
    private static readonly Single[] _mdctCos = BuildMdctCos();
    #endregion

    #region 属性
    /// <summary>512 点的分析滤波窗</summary>
    public static Single[] AnalysisWindow => _analysisWindow;

    /// <summary>多相分析的 32×64 旋转矩阵，按 [k * 64 + i] 存放</summary>
    public static Single[] Matrix => _matrix;

    /// <summary>36 点 MDCT 的正弦窗</summary>
    public static Single[] MdctWindow => _mdctWindow;

    /// <summary>36 点 MDCT 的余弦核，按 [k * 36 + n] 存放</summary>
    public static Single[] MdctCos => _mdctCos;
    #endregion

    #region 方法
    /// <summary>构造分析滤波窗</summary>
    /// <param name="source">综合窗定点系数</param>
    /// <param name="target">目标分析窗</param>
    /// <remarks>综合窗满足 D[511-n] = -D[n]，分析窗取相反数后与综合窗等价（仅相差全局符号）</remarks>
    public static void FillAnalysisWindow(Int32[] source, Single[] target)
    {
        for (var i = 0; i < target.Length; i++)
        {
            var j = i;
            var v = j < 256 ? source[j] : -source[511 - j];
            target[i] = v / 65536f;
        }
    }

    private static Single[] BuildAnalysisWindow()
    {
        var window = new Single[FilterLength];
        FillAnalysisWindow(Mp3Tables.SynthWindow, window);

        return window;
    }

    private static Single[] BuildMatrix()
    {
        var matrix = new Single[Subbands * 64];
        for (var k = 0; k < Subbands; k++)
        {
            for (var i = 0; i < 64; i++)
            {
                matrix[k * 64 + i] = (Single)Math.Cos((2 * k + 1) * (i - 16) * Math.PI / 64);
            }
        }

        return matrix;
    }

    private static Single[] BuildMdctWindow()
    {
        var window = new Single[BlockLength];
        for (var n = 0; n < BlockLength; n++)
        {
            window[n] = (Single)Math.Sin(Math.PI / BlockLength * (n + 0.5));
        }

        return window;
    }

    private static Single[] BuildMdctCos()
    {
        var cos = new Single[MdctLines * BlockLength];
        for (var k = 0; k < MdctLines; k++)
        {
            for (var n = 0; n < BlockLength; n++)
            {
                cos[k * BlockLength + n] = (Single)Math.Cos(Math.PI / 72 * (2 * n + 1 + 18) * (2 * k + 1));
            }
        }

        return cos;
    }
    #endregion
}

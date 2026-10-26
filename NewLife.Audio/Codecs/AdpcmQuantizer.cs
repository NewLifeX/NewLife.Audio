namespace NewLife.Audio.Codecs;

/// <summary>自适应差分脉冲编码量化器（多个 ADPCM 编解码器共用）</summary>
/// <remarks>
/// 采用「均匀中平量化 + 乘性步长自适应」：量化电平为步长的整数倍（四舍五入，夹到 ±半量程），
/// 预测值为电平步长乘积的累加；步长按量化幅度的归一化档位乘以 0.88~1.18 的倍数，
/// 幅度大则放大、幅度小则收缩，因此既能快速跟上突跳，也不会无限增长。
///
/// 早期版本的量化器存在两处错误：重建增益被折半（重建幅度系统性衰减，实测信噪比为负），
/// 以及步长只增不减（很快饱和到最大步长导致输出全是噪声）。满增益积分配合可升可降的步长，
/// 才能让重建幅度跟上输入。编码器与解码器必须使用同一套量化与自适应规则，且状态不能跨
/// 不配对调用延续，否则往返误差会线性累积。
/// </remarks>
internal sealed class AdpcmQuantizer
{
    #region 常量
    /// <summary>步长自适应倍数表，按归一化量化幅度查表；幅度大则放大步长、幅度小则收缩</summary>
    private static readonly Double[] StepGainTable = [0.88, 0.92, 0.96, 1.00, 1.02, 1.05, 1.10, 1.18];
    #endregion

    #region 属性
    private readonly Int32 _halfLevels;
    private readonly Int32 _maxLevel;

    /// <summary>当前量化步长</summary>
    private Double _step = 16;

    /// <summary>当前预测值</summary>
    private Double _predicted;
    #endregion

    /// <summary>以量化位数初始化量化器</summary>
    /// <param name="bits">每样本比特数，2~6</param>
    public AdpcmQuantizer(Int32 bits)
    {
        _halfLevels = 1 << (bits - 1);
        _maxLevel = _halfLevels - 1;
    }

    #region 方法
    /// <summary>编码一个样本，返回码字</summary>
    /// <param name="sample">输入样本</param>
    /// <returns>码字，取值范围 0 ~ 2^bits-1</returns>
    public Int32 Encode(Int32 sample)
    {
        var step = _step;
        var diff = sample - _predicted;

        // 中平量化：电平为步长整数倍，四舍五入后夹到 [-half, half-1]
        var level = (Int32)Math.Round(diff / step);
        if (level > _maxLevel) level = _maxLevel;
        if (level < -_halfLevels) level = -_halfLevels;

        _predicted += level * step;
        ClipPrediction();
        UpdateStep(level);

        return level + _halfLevels;
    }

    /// <summary>解码一个码字，返回重建样本</summary>
    /// <param name="code">码字，取值范围 0 ~ 2^bits-1</param>
    /// <returns>重建样本</returns>
    public Int16 Decode(Int32 code)
    {
        var step = _step;
        var level = code - _halfLevels;

        _predicted += level * step;
        ClipPrediction();
        UpdateStep(level);

        return (Int16)_predicted;
    }

    /// <summary>按量化幅度的归一化档位乘性调整步长，幅度越大步长增长越快</summary>
    /// <param name="level">量化电平</param>
    private void UpdateStep(Int32 level)
    {
        var magnitude = Math.Abs(level);
        if (magnitude > _maxLevel) magnitude = _maxLevel;

        // 把 0~maxLevel 归一化到倍数表下标 0~7，位数少的编码也能用满整张表
        var index = _maxLevel > 0 ? magnitude * (StepGainTable.Length - 1) / _maxLevel : 0;
        _step *= StepGainTable[index];

        if (_step < 1) _step = 1;
        if (_step > 32767) _step = 32767;
    }

    private void ClipPrediction()
    {
        if (_predicted > 32767) _predicted = 32767;
        if (_predicted < -32768) _predicted = -32768;
    }
    #endregion
}

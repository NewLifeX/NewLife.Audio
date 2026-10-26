using System.Collections.Concurrent;
using System.Numerics;

namespace NewLife.Audio.Codecs;

/// <summary>Vorbis MDCT/IMDCT。当前使用直接 DCT-IV，后续可替换为 FFT 实现</summary>
internal sealed class VorbisMdct
{
    private static readonly ConcurrentDictionary<Int32, Single[]> _cosineCache = new();
    private readonly Dictionary<Int32, Single[]> _workBuffers = [];

    /// <summary>反变换：N/2 个频谱系数 → N 个时域样本</summary>
    /// <param name="buffer">频谱数据，调用后原地写入时域样本</param>
    /// <param name="blockSize">块大小 N</param>
    public void Inverse(Single[] buffer, Int32 blockSize)
    {
        var m = blockSize >> 1;
        if (m < 2) throw new ArgumentOutOfRangeException(nameof(blockSize));

        var cosine = GetCosineTable(m);
        var work = GetWorkBuffer(m);

        for (var i = 0; i < m; i++)
        {
            var row = i * m;
            work[i] = Dot(buffer, 0, cosine, row, m);
        }

        var half = m >> 1;
        for (var i = 0; i < half; i++)
            buffer[i] = work[i + half];

        var threeQuarters = 3 * m / 2;
        for (var i = half; i < threeQuarters; i++)
            buffer[i] = -work[threeQuarters - 1 - i];

        for (var i = threeQuarters; i < 2 * m; i++)
            buffer[i] = -work[i - threeQuarters];
    }

    /// <summary>正变换：N 个时域样本 → N/2 个频谱系数</summary>
    /// <param name="buffer">时域样本，调用后前 N/2 个位置写入频谱系数</param>
    /// <param name="blockSize">块大小 N</param>
    public void Forward(Single[] buffer, Int32 blockSize)
    {
        var m = blockSize >> 1;
        if (m < 2) throw new ArgumentOutOfRangeException(nameof(blockSize));

        var cosine = GetCosineTable(m);
        var folded = GetWorkBuffer(m);

        var threeQuarters = 3 * m / 2;
        var half = m >> 1;
        for (var j = 0; j < m; j++)
        {
            var value = j >= half ? buffer[j - half] : 0f;
            value -= buffer[threeQuarters - 1 - j];
            if (j < half) value -= buffer[threeQuarters + j];
            folded[j] = value;
        }

        for (var k = 0; k < m; k++)
        {
            var row = k * m;
            buffer[k] = Dot(folded, 0, cosine, row, m);
        }
    }

    private static Single Dot(Single[] left, Int32 leftOffset, Single[] right, Int32 rightOffset, Int32 count)
    {
        var width = Vector<Single>.Count;
        var acc = Vector<Single>.Zero;
        var i = 0;
        for (; i <= count - width; i += width)
            acc += new Vector<Single>(left, leftOffset + i) * new Vector<Single>(right, rightOffset + i);

        var sum = Vector.Dot(acc, Vector<Single>.One);
        for (; i < count; i++)
            sum += left[leftOffset + i] * right[rightOffset + i];

        return sum;
    }

    private Single[] GetWorkBuffer(Int32 m)
    {
        if (!_workBuffers.TryGetValue(m, out var buffer))
        {
            buffer = new Single[m];
            _workBuffers[m] = buffer;
        }

        return buffer;
    }

    private static Single[] GetCosineTable(Int32 m) => _cosineCache.GetOrAdd(m, static size =>
    {
        var table = new Single[size * size];
        for (var i = 0; i < size; i++)
        {
            var angle = Math.PI / size * (i + 0.5);
            var row = i * size;
            for (var k = 0; k < size; k++)
                table[row + k] = (Single)Math.Cos(angle * (k + 0.5));
        }

        return table;
    });
}

using NewLife;

namespace NewLife.Audio.Codecs;

/// <summary>Vorbis 模式。包含块大小、窗形与映射</summary>
internal sealed class VorbisMode
{
    private const Single HalfPi = 3.1415926539f / 2;

    private readonly Single[][] _windows;
    private readonly Int32 _block0Size;
    private readonly Int32 _block1Size;
    private readonly Int32 _blockSize;
    private readonly Boolean _longBlock;

    private VorbisMode(Boolean longBlock, Int32 block0Size, Int32 block1Size, Int32 blockSize, Single[][] windows, VorbisMapping mapping)
    {
        _longBlock = longBlock;
        _block0Size = block0Size;
        _block1Size = block1Size;
        _blockSize = blockSize;
        _windows = windows;
        Mapping = mapping;
    }

    /// <summary>块大小</summary>
    public Int32 BlockSize => _blockSize;

    /// <summary>声道映射</summary>
    public VorbisMapping Mapping { get; }

    /// <summary>是否为长块</summary>
    public Boolean IsLongBlock => _longBlock;

    /// <summary>读取模式配置</summary>
    /// <param name="reader">位流读取器</param>
    /// <param name="block0Size">短块大小</param>
    /// <param name="block1Size">长块大小</param>
    /// <param name="mappings">映射集合</param>
    /// <returns>模式实例</returns>
    public static VorbisMode Read(ref VorbisBitReader reader, Int32 block0Size, Int32 block1Size, VorbisMapping[] mappings)
    {
        var longBlock = reader.ReadBit() != 0;
        if (reader.ReadBits(32) != 0) throw new InvalidDataException(L.T("The Vorbis mode has an invalid window or transform type."));

        var mappingIndex = (Int32)reader.ReadBits(8);
        if (mappingIndex >= mappings.Length) throw new InvalidDataException(L.T("The Vorbis mode mapping index is invalid."));

        Single[][] windows;
        if (longBlock)
        {
            windows =
            [
                CalcWindow(block0Size, block1Size, block0Size),
                CalcWindow(block1Size, block1Size, block0Size),
                CalcWindow(block0Size, block1Size, block1Size),
                CalcWindow(block1Size, block1Size, block1Size),
            ];
        }
        else
        {
            windows = [CalcWindow(block0Size, block0Size, block0Size)];
        }

        return new VorbisMode(longBlock, block0Size, block1Size, longBlock ? block1Size : block0Size, windows, mappings[mappingIndex]);
    }

    /// <summary>读取当前包的模式与窗形信息</summary>
    /// <param name="reader">位流读取器</param>
    /// <param name="windowIndex">窗形索引</param>
    /// <param name="startIndex">有效数据起始位置</param>
    /// <param name="validLength">有效数据结束位置</param>
    /// <param name="totalLength">总数据结束位置</param>
    /// <returns>是否需要解码</returns>
    public Boolean ReadPacketInfo(ref VorbisBitReader reader, out Int32 windowIndex, out Int32 startIndex, out Int32 validLength, out Int32 totalLength)
    {
        if (_longBlock)
        {
            var previousFlag = reader.ReadBit();
            var nextFlag = reader.ReadBit();
            windowIndex = previousFlag + (nextFlag << 1);

            var previousSize = previousFlag != 0 ? _block1Size : _block0Size;
            var nextSize = nextFlag != 0 ? _block1Size : _block0Size;
            startIndex = _blockSize / 4 - previousSize / 4;
            totalLength = _blockSize / 4 * 3 + nextSize / 4;
            validLength = totalLength - nextSize / 2;

            return true;
        }

        windowIndex = 0;
        startIndex = 0;
        validLength = _blockSize / 2;
        totalLength = _blockSize;
        return true;
    }

    /// <summary>取得窗形</summary>
    /// <param name="windowIndex">窗形索引</param>
    /// <returns>窗函数</returns>
    public Single[] GetWindow(Int32 windowIndex) => _windows[windowIndex];

    private static Single[] CalcWindow(Int32 previousBlockSize, Int32 blockSize, Int32 nextBlockSize)
    {
        var window = new Single[blockSize];

        var left = previousBlockSize / 2;
        var right = nextBlockSize / 2;

        var leftBegin = blockSize / 4 - left / 2;
        var rightBegin = blockSize - blockSize / 4 - right / 2;

        for (var i = 0; i < left; i++)
        {
            var x = (Single)Math.Sin((i + 0.5) / left * HalfPi);
            x *= x;
            window[leftBegin + i] = (Single)Math.Sin(x * HalfPi);
        }

        for (var i = leftBegin + left; i < rightBegin; i++)
            window[i] = 1f;

        for (var i = 0; i < right; i++)
        {
            var x = (Single)Math.Sin((right - i - 0.5) / right * HalfPi);
            x *= x;
            window[rightBegin + i] = (Single)Math.Sin(x * HalfPi);
        }

        return window;
    }
}

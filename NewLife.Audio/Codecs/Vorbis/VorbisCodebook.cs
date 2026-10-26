using NewLife;

namespace NewLife.Audio.Codecs;

/// <summary>Vorbis 码本。包含 Huffman 码表与可选的 VQ 查找表</summary>
internal sealed class VorbisCodebook
{
    private const Int32 MaxEntries = 1 << 20;
    private const Int32 MaxDimensions = 256;
    private const Int32 MaxLookupValues = 1 << 22;
    private const Int32 MaxTableBits = 10;

    private readonly Int32[] _lengths;
    private Single[] _lookup = [];
    private HuffmanEntry[] _prefix = [];
    private HuffmanEntry[] _overflow = [];
    private Int32 _tableBits;
    private Int32 _maxBits;

    private VorbisCodebook(Int32 dimensions, Int32 entries)
    {
        Dimensions = dimensions;
        Entries = entries;
        _lengths = new Int32[entries];
    }

    /// <summary>码本维度</summary>
    public Int32 Dimensions { get; }

    /// <summary>码本条目数</summary>
    public Int32 Entries { get; }

    /// <summary>查找表类型：0 无查找，1 隐式映射，2 显式映射</summary>
    public Int32 MapType { get; private set; }

    /// <summary>读取码本</summary>
    /// <param name="reader">位流读取器</param>
    /// <returns>码本实例</returns>
    public static VorbisCodebook Read(ref VorbisBitReader reader)
    {
        if (reader.ReadBits(24) != 0x564342) throw new InvalidDataException(L.T("The Vorbis codebook has an invalid signature."));

        var dimensions = (Int32)reader.ReadBits(16);
        var entries = (Int32)reader.ReadBits(24);
        if (dimensions < 1 || dimensions > MaxDimensions) throw new InvalidDataException(L.T("The Vorbis codebook has invalid dimensions."));
        if (entries < 1 || entries > MaxEntries) throw new InvalidDataException(L.T("The Vorbis codebook has an invalid entry count."));

        var book = new VorbisCodebook(dimensions, entries);
        book.ReadLengths(ref reader);
        book.BuildHuffman();
        book.ReadLookup(ref reader);

        return book;
    }

    /// <summary>根据条目号和维度取得查找值</summary>
    /// <param name="entry">条目号</param>
    /// <param name="dimension">维度</param>
    /// <returns>查找值</returns>
    public Single GetValue(Int32 entry, Int32 dimension)
    {
        if (MapType == 0) throw new InvalidOperationException(L.T("The Vorbis codebook has no lookup table."));

        return _lookup[entry * Dimensions + dimension];
    }

    /// <summary>解码一个 Huffman 标量</summary>
    /// <param name="reader">位流读取器</param>
    /// <returns>条目号，失败时返回 -1</returns>
    public Int32 DecodeScalar(ref VorbisBitReader reader)
    {
        var bits = reader.PeekBits(_tableBits, out var available);
        if (available <= 0) return -1;

        var entry = _prefix[(Int32)bits];
        if (entry.Length > 0)
        {
            if (entry.Length > available) return -1;

            reader.SkipBits(entry.Length);
            return entry.Symbol;
        }

        if (_overflow.Length == 0) return -1;

        bits = reader.PeekBits(_maxBits, out available);
        foreach (var item in _overflow)
        {
            if (item.Length > available) break;
            if ((bits & Mask(item.Length)) == item.Code)
            {
                reader.SkipBits(item.Length);
                return item.Symbol;
            }
        }

        return -1;
    }

    private static UInt32 Mask(Int32 bits) => bits >= 32 ? UInt32.MaxValue : (1U << bits) - 1;

    private void ReadLengths(ref VorbisBitReader reader)
    {
        if (reader.ReadBit() != 0)
        {
            // 有序码本：按长度递增，游程编码
            var currentLength = (Int32)reader.ReadBits(5) + 1;
            var currentEntry = 0;
            while (currentEntry < Entries)
            {
                if (currentLength > 32) throw new InvalidDataException(L.T("The Vorbis codebook has an invalid codeword length."));

                var count = (Int32)reader.ReadBits(ILog(Entries - currentEntry));
                if (count < 0 || currentEntry + count > Entries) throw new InvalidDataException(L.T("The Vorbis codebook has an invalid ordered length list."));

                for (var i = 0; i < count; i++)
                    _lengths[currentEntry++] = currentLength;

                currentLength++;
            }

            return;
        }

        // 无序码本：逐条读取，可稀疏
        var sparse = reader.ReadBit() != 0;
        for (var i = 0; i < Entries; i++)
        {
            if (!sparse || reader.ReadBit() != 0)
            {
                var length = (Int32)reader.ReadBits(5) + 1;
                if (length > 32) throw new InvalidDataException(L.T("The Vorbis codebook has an invalid codeword length."));
                _lengths[i] = length;
            }
        }
    }

    private void BuildHuffman()
    {
        var used = new List<HuffmanEntry>();

        var first = -1;
        for (var i = 0; i < _lengths.Length; i++)
        {
            if (_lengths[i] <= 0) continue;
            if (first < 0) first = i;
            _maxBits = Math.Max(_maxBits, _lengths[i]);
        }

        if (first < 0)
        {
            // 空码本：允许解码失败，但保留一个空表
            _tableBits = 0;
            _prefix = [];
            _overflow = [];
            return;
        }

        var available = new UInt32[33];

        AddUsed(used, first, 0, _lengths[first]);
        for (var i = 1; i <= _lengths[first]; i++)
            available[i] = 1U << (32 - i);

        for (var i = first + 1; i < _lengths.Length; i++)
        {
            var length = _lengths[i];
            if (length <= 0) continue;

            var z = length;
            while (z > 0 && available[z] == 0) z--;
            if (z == 0) throw new InvalidDataException(L.T("The Vorbis codebook has an overspecified Huffman tree."));

            var code = available[z];
            available[z] = 0;
            AddUsed(used, i, BitReverse(code), length);

            if (z != length)
            {
                for (var y = length; y > z; y--)
                    available[y] = code + (1U << (32 - y));
            }
        }

        _tableBits = Math.Min(_maxBits, MaxTableBits);
        var tableSize = 1 << _tableBits;
        _prefix = new HuffmanEntry[tableSize];

        var overflow = new List<HuffmanEntry>();
        foreach (var item in used)
        {
            if (item.Length <= _tableBits)
            {
                var step = 1 << item.Length;
                for (var index = (Int32)item.Code; index < tableSize; index += step)
                    _prefix[index] = item;
            }
            else
                overflow.Add(item);
        }

        if (overflow.Count > 1)
        {
            overflow.Sort(static (x, y) =>
            {
                var result = x.Length - y.Length;
                return result != 0 ? result : x.Code.CompareTo(y.Code);
            });
        }
        _overflow = [.. overflow];
    }

    private static void AddUsed(List<HuffmanEntry> used, Int32 symbol, UInt32 code, Int32 length)
        => used.Add(new HuffmanEntry { Symbol = symbol, Code = code, Length = length });

    private void ReadLookup(ref VorbisBitReader reader)
    {
        MapType = (Int32)reader.ReadBits(4);
        if (MapType == 0) return;
        if (MapType > 2) throw new InvalidDataException(L.T("The Vorbis codebook uses a reserved lookup type."));

        var minimum = Float32Unpack(reader.ReadUInt32());
        var delta = Float32Unpack(reader.ReadUInt32());
        var valueBits = (Int32)reader.ReadBits(4) + 1;
        var sequenceP = reader.ReadBit() != 0;

        var lookupValues = MapType == 1 ? Lookup1Values(Entries, Dimensions) : Entries * Dimensions;
        if (lookupValues <= 0 || lookupValues > MaxLookupValues) throw new InvalidDataException(L.T("The Vorbis codebook has an invalid lookup table size."));

        var multiplicands = new UInt32[lookupValues];
        for (var i = 0; i < lookupValues; i++)
            multiplicands[i] = reader.ReadBits(valueBits);

        _lookup = new Single[Entries * Dimensions];
        var lookup = _lookup;

        if (MapType == 1)
        {
            for (var entry = 0; entry < Entries; entry++)
            {
                var last = 0.0;
                var divisor = 1L;
                for (var dimension = 0; dimension < Dimensions; dimension++)
                {
                    var offset = (Int32)((entry / divisor) % lookupValues);
                    var value = multiplicands[offset] * (Double)delta + minimum + last;
                    lookup[entry * Dimensions + dimension] = (Single)value;

                    if (sequenceP) last = value;

                    divisor *= lookupValues;
                }
            }
        }
        else
        {
            var offset = 0;
            for (var entry = 0; entry < Entries; entry++)
            {
                var last = 0.0;
                for (var dimension = 0; dimension < Dimensions; dimension++)
                {
                    var value = multiplicands[offset++] * (Double)delta + minimum + last;
                    lookup[entry * Dimensions + dimension] = (Single)value;

                    if (sequenceP) last = value;
                }
            }
        }
    }

    private static Int32 Lookup1Values(Int32 entries, Int32 dimensions)
    {
        var value = (Int32)Math.Floor(Math.Exp(Math.Log(entries) / dimensions));
        if (Math.Pow(value + 1, dimensions) <= entries) value++;

        return value;
    }

    private static Single Float32Unpack(UInt32 bits)
    {
        var sign = (Int32)(bits >> 31);
        var exponent = (Int32)((bits & 0x7FE00000) >> 21) - 788;
        var mantissa = (Int32)(bits & 0x1FFFFF);
        if (sign != 0) mantissa = -mantissa;

        return (Single)(mantissa * Math.Pow(2.0, exponent));
    }

    /// <summary>计算无符号整数的位长（ilog）</summary>
    /// <param name="value">无符号整数</param>
    /// <returns>位长，0 返回 0</returns>
    public static Int32 ILog(Int32 value)
    {
        var count = 0;
        while (value > 0)
        {
            count++;
            value >>= 1;
        }
        return count;
    }

    private static UInt32 BitReverse(UInt32 value)
    {
        value = ((value & 0xAAAAAAAA) >> 1) | ((value & 0x55555555) << 1);
        value = ((value & 0xCCCCCCCC) >> 2) | ((value & 0x33333333) << 2);
        value = ((value & 0xF0F0F0F0) >> 4) | ((value & 0x0F0F0F0F) << 4);
        value = ((value & 0xFF00FF00) >> 8) | ((value & 0x00FF00FF) << 8);

        return (value >> 16) | (value << 16);
    }

    private struct HuffmanEntry
    {
        public Int32 Symbol;
        public UInt32 Code;
        public Int32 Length;
    }
}

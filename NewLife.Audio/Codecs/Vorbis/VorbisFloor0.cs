using NewLife;

namespace NewLife.Audio.Codecs;

/// <summary>Vorbis floor 0。LSP 系数与 Bark 曲线</summary>
internal sealed class VorbisFloor0 : VorbisFloor
{
    private readonly Int32 _order;
    private readonly Int32 _rate;
    private readonly Int32 _barkMapSize;
    private readonly Int32 _amplitudeBits;
    private readonly Int32 _amplitudeOffset;
    private readonly Int32 _amplitudeDivisor;
    private readonly Int32 _bookBits;
    private readonly VorbisCodebook[] _books;
    private readonly Dictionary<Int32, Int32[]> _barkMaps = [];
    private readonly Dictionary<Int32, Single[]> _wMaps = [];

    private VorbisFloor0(Int32 order, Int32 rate, Int32 barkMapSize, Int32 amplitudeBits, Int32 amplitudeOffset,
        VorbisCodebook[] books, Int32 block0Size, Int32 block1Size)
    {
        _order = order;
        _rate = rate;
        _barkMapSize = barkMapSize;
        _amplitudeBits = amplitudeBits;
        _amplitudeOffset = amplitudeOffset;
        _amplitudeDivisor = (1 << amplitudeBits) - 1;
        _books = books;
        _bookBits = VorbisCodebook.ILog(books.Length);

        _barkMaps[block0Size] = SynthesizeBarkCurve(block0Size / 2);
        _barkMaps[block1Size] = SynthesizeBarkCurve(block1Size / 2);
        _wMaps[block0Size] = SynthesizeWDelMap(block0Size / 2);
        _wMaps[block1Size] = SynthesizeWDelMap(block1Size / 2);
    }

    /// <summary>读取 floor 0 配置</summary>
    /// <param name="reader">位流读取器</param>
    /// <param name="codebooks">码本集合</param>
    /// <param name="block0Size">短块大小</param>
    /// <param name="block1Size">长块大小</param>
    /// <returns>floor 0 实例</returns>
    public static VorbisFloor0 Read(ref VorbisBitReader reader, VorbisCodebook[] codebooks, Int32 block0Size, Int32 block1Size)
    {
        var order = (Int32)reader.ReadBits(8);
        var rate = (Int32)reader.ReadBits(16);
        var barkMapSize = (Int32)reader.ReadBits(16);
        var amplitudeBits = (Int32)reader.ReadBits(6);
        var amplitudeOffset = (Int32)reader.ReadBits(8);
        var bookCount = (Int32)reader.ReadBits(4) + 1;

        if (order < 1 || rate < 1 || barkMapSize < 1 || amplitudeBits < 1 || amplitudeBits > 30 || bookCount < 1)
            throw new InvalidDataException(L.T("The Vorbis floor0 configuration is invalid."));

        var books = new VorbisCodebook[bookCount];
        for (var i = 0; i < bookCount; i++)
        {
            var index = (Int32)reader.ReadBits(8);
            if (index >= codebooks.Length) throw new InvalidDataException(L.T("The Vorbis floor0 codebook index is invalid."));

            var book = codebooks[index];
            if (book.MapType == 0) throw new InvalidDataException(L.T("The Vorbis floor0 codebook has no lookup table."));

            books[i] = book;
        }

        return new VorbisFloor0(order, rate, barkMapSize, amplitudeBits, amplitudeOffset, books, block0Size, block1Size);
    }

    /// <summary>解析当前包中的 floor 0 数据</summary>
    /// <param name="reader">位流读取器</param>
    /// <param name="blockSize">块大小</param>
    /// <param name="channel">声道号</param>
    /// <returns>floor 数据</returns>
    public override VorbisFloorData Unpack(ref VorbisBitReader reader, Int32 blockSize, Int32 channel)
    {
        var data = new VorbisFloorData
        {
            Coeff = new Single[_order + 1],
        };

        var amplitude = (Int32)reader.ReadBits(_amplitudeBits);
        if (amplitude <= 0) return data;

        data.Amplitude = (Single)amplitude / _amplitudeDivisor * _amplitudeOffset;

        var bookIndex = (Int32)reader.ReadBits(_bookBits);
        if (bookIndex >= _books.Length)
        {
            data.Amplitude = 0;
            return data;
        }

        var book = _books[bookIndex];
        for (var i = 0; i < _order;)
        {
            var entry = book.DecodeScalar(ref reader);
            if (entry < 0)
            {
                data.Amplitude = 0;
                return data;
            }

            for (var j = 0; i < _order && j < book.Dimensions; j++, i++)
                data.Coeff[i] = book.GetValue(entry, j);
        }

        var last = 0f;
        for (var j = 0; j < _order;)
        {
            for (var k = 0; j < _order && k < book.Dimensions; j++, k++)
                data.Coeff[j] += last;

            last = data.Coeff[j - 1];
        }

        data.HasEnergy = true;

        return data;
    }

    /// <summary>把 floor 曲线乘到残差向量上</summary>
    /// <param name="data">地板数据</param>
    /// <param name="blockSize">块大小</param>
    /// <param name="residue">残差向量</param>
    public override void Apply(VorbisFloorData data, Int32 blockSize, Single[] residue)
    {
        var n = blockSize / 2;
        if (data.Amplitude <= 0f)
        {
            Array.Clear(residue, 0, n);
            return;
        }

        var barkMap = _barkMaps[blockSize];
        var wMap = _wMaps[blockSize];
        var coefficients = data.Coeff;

        for (var i = 0; i < _order; i++)
            coefficients[i] = 2f * (Single)Math.Cos(coefficients[i]);

        var index = 0;
        while (index < n)
        {
            var bark = barkMap[index];
            var p = 0.5f;
            var q = 0.5f;
            var w = wMap[bark];
            var j = 1;
            for (; j < _order; j += 2)
            {
                q *= w - coefficients[j - 1];
                p *= w - coefficients[j];
            }

            if (j == _order)
            {
                q *= w - coefficients[j - 1];
                p *= p * (4f - w * w);
                q *= q;
            }
            else
            {
                p *= p * (2f - w);
                q *= q * (2f + w);
            }

            q = data.Amplitude / (Single)Math.Sqrt(p + q) - _amplitudeOffset;
            q = (Single)Math.Exp(q * 0.11512925f);

            residue[index] *= q;
            while (++index < n && barkMap[index] == bark)
                residue[index] *= q;
        }
    }

    private Int32[] SynthesizeBarkCurve(Int32 n)
    {
        var scale = _barkMapSize / ToBark(_rate / 2f);
        var map = new Int32[n + 1];

        for (var i = 0; i < n - 1; i++)
            map[i] = Math.Min(_barkMapSize - 1, (Int32)Math.Floor(ToBark((_rate / 2f) / n * i) * scale));

        map[n] = -1;
        return map;
    }

    private Single[] SynthesizeWDelMap(Int32 n)
    {
        var wdel = Math.PI / _barkMapSize;
        var map = new Single[n];
        for (var i = 0; i < n; i++)
            map[i] = 2f * (Single)Math.Cos(wdel * i);

        return map;
    }

    private static Single ToBark(Single lsp)
        => 13.1f * (Single)Math.Atan(0.00074 * lsp) + 2.24f * (Single)Math.Atan(0.0000000185 * lsp * lsp) + 0.0001f * lsp;
}

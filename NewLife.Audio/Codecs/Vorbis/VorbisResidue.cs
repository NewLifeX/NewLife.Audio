using NewLife;

namespace NewLife.Audio.Codecs;

/// <summary>Vorbis 残差。支持 residue 类型 0/1/2</summary>
internal sealed class VorbisResidue
{
    private Int32 _channels;
    private Int32 _begin;
    private Int32 _end;
    private Int32 _partitionSize;
    private Int32 _maxStages;
    private Boolean _interleaved;
    private VorbisCodebook _classBook = null!;
    private Int32[] _cascade = [];
    private VorbisCodebook[][] _books = [];
    private Int32[][] _decodeMap = [];

    /// <summary>残差类型</summary>
    public Int32 Type { get; private set; }

    /// <summary>读取残差配置</summary>
    /// <param name="reader">位流读取器</param>
    /// <param name="channels">声道数</param>
    /// <param name="codebooks">码本集合</param>
    /// <returns>残差实例</returns>
    public static VorbisResidue Read(ref VorbisBitReader reader, Int32 channels, VorbisCodebook[] codebooks)
    {
        var type = (Int32)reader.ReadBits(16);
        if (type > 2) throw new InvalidDataException(L.T("The Vorbis residue type is invalid."));

        var begin = (Int32)reader.ReadBits(24);
        var end = (Int32)reader.ReadBits(24);
        var partitionSize = (Int32)reader.ReadBits(24) + 1;
        var classifications = (Int32)reader.ReadBits(6) + 1;
        var classBookIndex = (Int32)reader.ReadBits(8);
        if (classBookIndex >= codebooks.Length) throw new InvalidDataException(L.T("The Vorbis residue classbook index is invalid."));

        var residue = new VorbisResidue
        {
            Type = type,
            _channels = channels,
            _begin = begin,
            _end = end,
            _partitionSize = partitionSize,
            _classBook = codebooks[classBookIndex],
            _interleaved = type == 2,
        };

        var cascade = new Int32[classifications];
        var bookCount = 0;
        for (var i = 0; i < classifications; i++)
        {
            var lowBits = (Int32)reader.ReadBits(3);
            if (reader.ReadBit() != 0)
                cascade[i] = ((Int32)reader.ReadBits(5) << 3) | lowBits;
            else
                cascade[i] = lowBits;

            bookCount += CountBits(cascade[i]);
        }
        residue._cascade = cascade;

        var bookNumbers = new Int32[bookCount];
        for (var i = 0; i < bookNumbers.Length; i++)
        {
            var index = (Int32)reader.ReadBits(8);
            if (index >= codebooks.Length) throw new InvalidDataException(L.T("The Vorbis residue codebook index is invalid."));
            if (codebooks[index].MapType == 0) throw new InvalidDataException(L.T("The Vorbis residue codebook has no lookup table."));

            bookNumbers[i] = index;
        }

        var dimensions = residue._classBook.Dimensions;
        var partValues = 1L;
        for (var i = 0; i < dimensions; i++)
        {
            partValues *= classifications;
            if (partValues > residue._classBook.Entries || partValues > 1 << 20)
                throw new InvalidDataException(L.T("The Vorbis residue partition value count is invalid."));
        }

        var decodeMap = new Int32[partValues][];
        for (var i = 0; i < decodeMap.Length; i++)
        {
            var value = i;
            var divisor = (Int32)partValues / classifications;
            decodeMap[i] = new Int32[dimensions];
            for (var j = 0; j < dimensions; j++)
            {
                var digit = value / divisor;
                value -= digit * divisor;
                divisor /= classifications;
                decodeMap[i][j] = digit;
            }
        }
        residue._decodeMap = decodeMap;

        var books = new VorbisCodebook[classifications][];
        var bookIndex = 0;
        var maxStages = 0;
        for (var i = 0; i < classifications; i++)
        {
            var stages = VorbisCodebook.ILog(cascade[i]);
            books[i] = new VorbisCodebook[stages];
            if (stages > 0)
            {
                if (stages > maxStages) maxStages = stages;
                for (var stage = 0; stage < stages; stage++)
                {
                    if ((cascade[i] & (1 << stage)) != 0)
                        books[i][stage] = codebooks[bookNumbers[bookIndex++]];
                }
            }
        }
        residue._books = books;
        residue._maxStages = maxStages;

        return residue;
    }

    /// <summary>解码残差并累加到频谱向量</summary>
    /// <param name="reader">位流读取器</param>
    /// <param name="doNotDecode">各声道是否跳过</param>
    /// <param name="blockSize">块大小</param>
    /// <param name="buffer">频谱向量</param>
    public void Decode(ref VorbisBitReader reader, Boolean[] doNotDecode, Int32 blockSize, Single[][] buffer)
    {
        var interleavedChannels = _interleaved ? 1 : _channels;
        var effectiveBlockSize = _interleaved ? blockSize * _channels : blockSize;
        var end = Math.Min(_end, effectiveBlockSize / 2);
        var n = end - _begin;
        if (n <= 0) return;

        var anyDecoded = false;
        for (var i = 0; i < doNotDecode.Length; i++)
        {
            if (!doNotDecode[i])
            {
                anyDecoded = true;
                break;
            }
        }
        if (!anyDecoded) return;

        var partitionCount = n / _partitionSize;
        if (partitionCount <= 0) return;

        var partitionWords = (partitionCount + _classBook.Dimensions - 1) / _classBook.Dimensions;
        var classWords = new Int32[interleavedChannels * partitionWords][];

        for (var stage = 0; stage < _maxStages; stage++)
        {
            for (var partitionIndex = 0; partitionIndex < partitionCount;)
            {
                var entryIndex = partitionIndex / _classBook.Dimensions;
                if (stage == 0)
                {
                    for (var channel = 0; channel < interleavedChannels; channel++)
                    {
                        var value = _classBook.DecodeScalar(ref reader);
                        if (value < 0 || value >= _decodeMap.Length)
                            return;

                        classWords[channel * partitionWords + entryIndex] = _decodeMap[value];
                    }
                }

                for (var dimension = 0; dimension < _classBook.Dimensions && partitionIndex < partitionCount; dimension++, partitionIndex++)
                {
                    var offset = _begin + partitionIndex * _partitionSize;
                    for (var channel = 0; channel < interleavedChannels; channel++)
                    {
                        var classWord = classWords[channel * partitionWords + entryIndex];
                        if (classWord == null) return;

                        var classification = classWord[dimension];
                        if ((_cascade[classification] & (1 << stage)) == 0) continue;

                        var book = _books[classification][stage];
                        if (book == null) continue;

                        if (!WriteVectors(book, ref reader, buffer, channel, offset, _partitionSize))
                            return;
                    }
                }
            }
        }
    }

    private Boolean WriteVectors(VorbisCodebook book, ref VorbisBitReader reader, Single[][] residue, Int32 channel, Int32 offset, Int32 partitionSize)
    {
        if (_interleaved)
        {
            var channelIndex = offset % _channels;
            var bin = offset / _channels;

            for (var count = 0; count < partitionSize;)
            {
                var entry = book.DecodeScalar(ref reader);
                if (entry < 0) return false;

                for (var dimension = 0; dimension < book.Dimensions; dimension++, count++)
                {
                    residue[channelIndex][bin] += book.GetValue(entry, dimension);
                    if (++channelIndex == _channels)
                    {
                        channelIndex = 0;
                        bin++;
                    }
                }
            }

            return true;
        }

        if (Type == 1)
        {
            var target = residue[channel];
            for (var index = 0; index < partitionSize;)
            {
                var entry = book.DecodeScalar(ref reader);
                if (entry < 0) return false;

                for (var dimension = 0; dimension < book.Dimensions; dimension++, index++)
                    target[offset + index] += book.GetValue(entry, dimension);
            }

            return true;
        }

        var steps = partitionSize / book.Dimensions;
        if (steps <= 0) return false;

        var entries = new Int32[steps];
        for (var i = 0; i < steps; i++)
        {
            var entry = book.DecodeScalar(ref reader);
            if (entry < 0) return false;
            entries[i] = entry;
        }

        for (var dimension = 0; dimension < book.Dimensions; dimension++)
        {
            for (var step = 0; step < steps; step++)
                residue[channel][offset++] += book.GetValue(entries[step], dimension);
        }

        return true;
    }

    private static Int32 CountBits(Int32 value)
    {
        var count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }
        return count;
    }
}

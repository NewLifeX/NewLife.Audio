using NewLife;

namespace NewLife.Audio.Codecs;

/// <summary>Vorbis floor 1。分段线性插值的 dB 幅度曲线</summary>
internal sealed class VorbisFloor1 : VorbisFloor
{
    private static readonly Int32[] RangeLookup = [256, 128, 86, 64];
    private static readonly Int32[] YBitsLookup = [8, 7, 7, 6];

    private readonly Int32[] _partitionClass;
    private readonly Int32[] _classDimensions;
    private readonly Int32[] _classSubclasses;
    private readonly VorbisCodebook[] _classMasterbooks;
    private readonly VorbisCodebook[][] _subclassBooks;
    private readonly Int32[] _xList;
    private readonly Int32[] _lowNeighbor;
    private readonly Int32[] _highNeighbor;
    private readonly Int32[] _sortIndex;
    private readonly Int32 _multiplier;
    private readonly Int32 _range;
    private readonly Int32 _yBits;
    private readonly Int32 _maxPosts;

    private VorbisFloor1(Int32[] partitionClass, Int32[] classDimensions, Int32[] classSubclasses, VorbisCodebook[] classMasterbooks,
        VorbisCodebook[][] subclassBooks, Int32[] xList, Int32 multiplier, Int32 range, Int32 yBits)
    {
        _partitionClass = partitionClass;
        _classDimensions = classDimensions;
        _classSubclasses = classSubclasses;
        _classMasterbooks = classMasterbooks;
        _subclassBooks = subclassBooks;
        _xList = xList;
        _multiplier = multiplier;
        _range = range;
        _yBits = yBits;
        _maxPosts = 2 + partitionClass.Length * 8;
        _lowNeighbor = new Int32[xList.Length];
        _highNeighbor = new Int32[xList.Length];
        _sortIndex = new Int32[xList.Length];

        BuildNeighbors();
    }

    /// <summary>读取 floor 1 配置</summary>
    /// <param name="reader">位流读取器</param>
    /// <param name="codebooks">码本集合</param>
    /// <returns>floor 1 实例</returns>
    public static VorbisFloor1 Read(ref VorbisBitReader reader, VorbisCodebook[] codebooks)
    {
        var partitionCount = (Int32)reader.ReadBits(5);
        var partitionClass = new Int32[partitionCount];
        var maximumClass = -1;
        for (var i = 0; i < partitionCount; i++)
        {
            partitionClass[i] = (Int32)reader.ReadBits(4);
            if (partitionClass[i] > maximumClass) maximumClass = partitionClass[i];
        }

        maximumClass++;
        var classDimensions = new Int32[maximumClass];
        var classSubclasses = new Int32[maximumClass];
        var classMasterbooks = new VorbisCodebook[maximumClass];
        var subclassBooks = new VorbisCodebook[maximumClass][];
        for (var i = 0; i < maximumClass; i++)
        {
            classDimensions[i] = (Int32)reader.ReadBits(3) + 1;
            classSubclasses[i] = (Int32)reader.ReadBits(2);
            if (classSubclasses[i] > 0)
            {
                var bookIndex = (Int32)reader.ReadBits(8);
                if (bookIndex >= codebooks.Length) throw new InvalidDataException(L.T("The Vorbis floor1 master codebook index is invalid."));
                classMasterbooks[i] = codebooks[bookIndex];
            }

            var bookCount = 1 << classSubclasses[i];
            subclassBooks[i] = new VorbisCodebook[bookCount];
            for (var j = 0; j < bookCount; j++)
            {
                var bookIndex = (Int32)reader.ReadBits(8) - 1;
                if (bookIndex >= 0)
                {
                    if (bookIndex >= codebooks.Length) throw new InvalidDataException(L.T("The Vorbis floor1 subclass codebook index is invalid."));
                    subclassBooks[i][j] = codebooks[bookIndex];
                }
            }
        }

        var multiplierBits = (Int32)reader.ReadBits(2);
        var range = RangeLookup[multiplierBits];
        var yBits = YBitsLookup[multiplierBits];
        var multiplier = multiplierBits + 1;

        var rangeBits = (Int32)reader.ReadBits(4);
        var xList = new List<Int32>(2 + partitionCount * 8) { 0, 1 << rangeBits };
        for (var i = 0; i < partitionCount; i++)
        {
            var classNumber = partitionClass[i];
            for (var j = 0; j < classDimensions[classNumber]; j++)
            {
                var value = (Int32)reader.ReadBits(rangeBits);
                if (value < 0 || value > 1 << rangeBits) throw new InvalidDataException(L.T("The Vorbis floor1 x coordinate is invalid."));
                xList.Add(value);
            }
        }

        return new VorbisFloor1(partitionClass, classDimensions, classSubclasses, classMasterbooks, subclassBooks,
            [.. xList], multiplier, range, yBits);
    }

    /// <summary>解析当前包中的 floor 1 数据</summary>
    /// <param name="reader">位流读取器</param>
    /// <param name="blockSize">块大小</param>
    /// <param name="channel">声道号</param>
    /// <returns>floor 数据</returns>
    public override VorbisFloorData Unpack(ref VorbisBitReader reader, Int32 blockSize, Int32 channel)
    {
        var data = new VorbisFloorData { Posts = new Int32[_maxPosts] };

        if (reader.ReadBit() == 0) return data;

        var postCount = 2;
        data.Posts[0] = (Int32)reader.ReadBits(_yBits);
        data.Posts[1] = (Int32)reader.ReadBits(_yBits);

        for (var i = 0; i < _partitionClass.Length; i++)
        {
            var classNumber = _partitionClass[i];
            var dimensions = _classDimensions[classNumber];
            var subclassBits = _classSubclasses[classNumber];
            var subclassMask = (1 << subclassBits) - 1;
            var classValue = 0U;

            if (subclassBits > 0)
            {
                var masterbook = _classMasterbooks[classNumber];
                if (masterbook == null)
                {
                    postCount = 0;
                    break;
                }

                var value = masterbook.DecodeScalar(ref reader);
                if (value < 0)
                {
                    postCount = 0;
                    break;
                }
                classValue = (UInt32)value;
            }

            for (var j = 0; j < dimensions; j++)
            {
                var book = _subclassBooks[classNumber][(Int32)(classValue & subclassMask)];
                classValue >>= subclassBits;

                if (book != null)
                {
                    var value = book.DecodeScalar(ref reader);
                    if (value < 0)
                    {
                        postCount = 0;
                        break;
                    }
                    data.Posts[postCount] = value;
                }

                postCount++;
                if (postCount > data.Posts.Length)
                {
                    postCount = 0;
                    break;
                }
            }

            if (postCount == 0) break;
        }

        data.PostCount = postCount;
        data.HasEnergy = postCount > 0;

        return data;
    }

    /// <summary>把 floor 曲线乘到残差向量上</summary>
    /// <param name="data">地板数据</param>
    /// <param name="blockSize">块大小</param>
    /// <param name="residue">残差向量</param>
    public override void Apply(VorbisFloorData data, Int32 blockSize, Single[] residue)
    {
        var n = blockSize / 2;
        if (data.PostCount <= 0)
        {
            Array.Clear(residue, 0, n);
            return;
        }

        var stepFlags = UnwrapPosts(data);

        var lx = 0;
        var ly = data.Posts[0] * _multiplier;
        for (var i = 1; i < data.PostCount; i++)
        {
            var index = _sortIndex[i];
            if (stepFlags[index])
            {
                var hx = _xList[index];
                var hy = data.Posts[index] * _multiplier;
                if (lx < n)
                {
                    var end = Math.Min(hx, n);
                    if (end > lx) RenderLineMulti(lx, ly, end, hy, residue);
                }
                lx = hx;
                ly = hy;
            }

            if (lx >= n) break;
        }

        if (lx < n) RenderLineMulti(lx, ly, n, ly, residue);
    }

    private Boolean[] UnwrapPosts(VorbisFloorData data)
    {
        var stepFlags = new Boolean[_maxPosts];
        var finalY = new Int32[_maxPosts];
        stepFlags[0] = true;
        stepFlags[1] = true;
        finalY[0] = data.Posts[0];
        finalY[1] = data.Posts[1];

        for (var i = 2; i < data.PostCount; i++)
        {
            var lowIndex = _lowNeighbor[i];
            var highIndex = _highNeighbor[i];
            var predicted = RenderPoint(_xList[lowIndex], finalY[lowIndex], _xList[highIndex], finalY[highIndex], _xList[i]);
            var value = data.Posts[i];
            var highRoom = _range - predicted;
            var lowRoom = predicted;
            var room = Math.Min(highRoom, lowRoom) * 2;

            if (value != 0)
            {
                stepFlags[lowIndex] = true;
                stepFlags[highIndex] = true;
                stepFlags[i] = true;

                if (value >= room)
                    finalY[i] = highRoom > lowRoom ? value - lowRoom + predicted : predicted - value + highRoom - 1;
                else if ((value & 1) != 0)
                    finalY[i] = predicted - (value + 1) / 2;
                else
                    finalY[i] = predicted + value / 2;
            }
            else
            {
                stepFlags[i] = false;
                finalY[i] = predicted;
            }
        }

        for (var i = 0; i < data.PostCount; i++)
            data.Posts[i] = finalY[i];

        return stepFlags;
    }

    private static Int32 RenderPoint(Int32 x0, Int32 y0, Int32 x1, Int32 y1, Int32 x)
    {
        var dy = y1 - y0;
        var adx = x1 - x0;
        var ady = Math.Abs(dy);
        var error = ady * (x - x0);
        var offset = error / adx;

        return dy < 0 ? y0 - offset : y0 + offset;
    }

    private void RenderLineMulti(Int32 x0, Int32 y0, Int32 x1, Int32 y1, Single[] values)
    {
        var dy = y1 - y0;
        var adx = x1 - x0;
        if (adx <= 0) return;

        var ady = Math.Abs(dy);
        var step = 1 - (((dy >> 31) & 1) * 2);
        var baseY = dy / adx;
        var x = x0;
        var y = y0;
        var error = -adx;

        values[x0] *= InverseDbTable[ClampY(y0)];
        ady -= Math.Abs(baseY) * adx;

        while (++x < x1)
        {
            y += baseY;
            error += ady;
            if (error >= 0)
            {
                error -= adx;
                y += step;
            }

            values[x] *= InverseDbTable[ClampY(y)];
        }
    }

    private static Int32 ClampY(Int32 value) => value < 0 ? 0 : value > 255 ? 255 : value;

    /// <summary>把线性幅度转换为 floor1 的 Y 值</summary>
    /// <param name="amplitude">线性幅度</param>
    /// <returns>0~255 的 Y 值</returns>
    public static Int32 AmplitudeToY(Single amplitude)
    {
        if (amplitude <= InverseDbTable[0]) return 0;
        if (amplitude >= InverseDbTable[255]) return 255;

        var low = 0;
        var high = 255;
        while (low < high)
        {
            var middle = (low + high) >> 1;
            if (InverseDbTable[middle] < amplitude)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    /// <summary>取得 floor1 的 Y 值对应的线性幅度</summary>
    /// <param name="y">Y 值</param>
    /// <returns>线性幅度</returns>
    public static Single GetAmplitude(Int32 y) => InverseDbTable[ClampY(y)];

    private void BuildNeighbors()
    {
        _sortIndex[0] = 0;
        _sortIndex[1] = 1;
        for (var i = 2; i < _lowNeighbor.Length; i++)
        {
            _lowNeighbor[i] = 0;
            _highNeighbor[i] = 1;
            _sortIndex[i] = i;
            for (var j = 2; j < i; j++)
            {
                var value = _xList[j];
                if (value == _xList[i]) throw new InvalidDataException(L.T("The Vorbis floor1 x coordinate is duplicated."));

                if (value < _xList[i])
                {
                    if (value > _xList[_lowNeighbor[i]]) _lowNeighbor[i] = j;
                }
                else if (value < _xList[_highNeighbor[i]])
                    _highNeighbor[i] = j;
            }
        }

        for (var i = 0; i < _sortIndex.Length - 1; i++)
        {
            for (var j = i + 1; j < _sortIndex.Length; j++)
            {
                if (_xList[_sortIndex[i]] > _xList[_sortIndex[j]])
                {
                    var temp = _sortIndex[i];
                    _sortIndex[i] = _sortIndex[j];
                    _sortIndex[j] = temp;
                }
            }
        }
    }

    private static readonly Single[] InverseDbTable =
    [
            1.0649863e-07f, 1.1341951e-07f, 1.2079015e-07f, 1.2863978e-07f,
            1.3699951e-07f, 1.4590251e-07f, 1.5538408e-07f, 1.6548181e-07f,
            1.7623575e-07f, 1.8768855e-07f, 1.9988561e-07f, 2.1287530e-07f,
            2.2670913e-07f, 2.4144197e-07f, 2.5713223e-07f, 2.7384213e-07f,
            2.9163793e-07f, 3.1059021e-07f, 3.3077411e-07f, 3.5226968e-07f,
            3.7516214e-07f, 3.9954229e-07f, 4.2550680e-07f, 4.5315863e-07f,
            4.8260743e-07f, 5.1396998e-07f, 5.4737065e-07f, 5.8294187e-07f,
            6.2082472e-07f, 6.6116941e-07f, 7.0413592e-07f, 7.4989464e-07f,
            7.9862701e-07f, 8.5052630e-07f, 9.0579828e-07f, 9.6466216e-07f,
            1.0273513e-06f, 1.0941144e-06f, 1.1652161e-06f, 1.2409384e-06f,
            1.3215816e-06f, 1.4074654e-06f, 1.4989305e-06f, 1.5963394e-06f,
            1.7000785e-06f, 1.8105592e-06f, 1.9282195e-06f, 2.0535261e-06f,
            2.1869758e-06f, 2.3290978e-06f, 2.4804557e-06f, 2.6416497e-06f,
            2.8133190e-06f, 2.9961443e-06f, 3.1908506e-06f, 3.3982101e-06f,
            3.6190449e-06f, 3.8542308e-06f, 4.1047004e-06f, 4.3714470e-06f,
            4.6555282e-06f, 4.9580707e-06f, 5.2802740e-06f, 5.6234160e-06f,
            5.9888572e-06f, 6.3780469e-06f, 6.7925283e-06f, 7.2339451e-06f,
            7.7040476e-06f, 8.2047000e-06f, 8.7378876e-06f, 9.3057248e-06f,
            9.9104632e-06f, 1.0554501e-05f, 1.1240392e-05f, 1.1970856e-05f,
            1.2748789e-05f, 1.3577278e-05f, 1.4459606e-05f, 1.5399272e-05f,
            1.6400004e-05f, 1.7465768e-05f, 1.8600792e-05f, 1.9809576e-05f,
            2.1096914e-05f, 2.2467911e-05f, 2.3928002e-05f, 2.5482978e-05f,
            2.7139006e-05f, 2.8902651e-05f, 3.0780908e-05f, 3.2781225e-05f,
            3.4911534e-05f, 3.7180282e-05f, 3.9596466e-05f, 4.2169667e-05f,
            4.4910090e-05f, 4.7828601e-05f, 5.0936773e-05f, 5.4246931e-05f,
            5.7772202e-05f, 6.1526565e-05f, 6.5524908e-05f, 6.9783085e-05f,
            7.4317983e-05f, 7.9147585e-05f, 8.4291040e-05f, 8.9768747e-05f,
            9.5602426e-05f, 0.00010181521f, 0.00010843174f, 0.00011547824f,
            0.00012298267f, 0.00013097477f, 0.00013948625f, 0.00014855085f,
            0.00015820453f, 0.00016848555f, 0.00017943469f, 0.00019109536f,
            0.00020351382f, 0.00021673929f, 0.00023082423f, 0.00024582449f,
            0.00026179955f, 0.00027881276f, 0.00029693158f, 0.00031622787f,
            0.00033677814f, 0.00035866388f, 0.00038197188f, 0.00040679456f,
            0.00043323036f, 0.00046138411f, 0.00049136745f, 0.00052329927f,
            0.00055730621f, 0.00059352311f, 0.00063209358f, 0.00067317058f,
            0.00071691700f, 0.00076350630f, 0.00081312324f, 0.00086596457f,
            0.00092223983f, 0.00098217216f, 0.0010459992f, 0.0011139742f,
            0.0011863665f, 0.0012634633f, 0.0013455702f, 0.0014330129f,
            0.0015261382f, 0.0016253153f, 0.0017309374f, 0.0018434235f,
            0.0019632195f, 0.0020908006f, 0.0022266726f, 0.0023713743f,
            0.0025254795f, 0.0026895994f, 0.0028643847f, 0.0030505286f,
            0.0032487691f, 0.0034598925f, 0.0036847358f, 0.0039241906f,
            0.0041792066f, 0.0044507950f, 0.0047400328f, 0.0050480668f,
            0.0053761186f, 0.0057254891f, 0.0060975636f, 0.0064938176f,
            0.0069158225f, 0.0073652516f, 0.0078438871f, 0.0083536271f,
            0.0088964928f, 0.009474637f, 0.010090352f, 0.010746080f,
            0.011444421f, 0.012188144f, 0.012980198f, 0.013823725f,
            0.014722068f, 0.015678791f, 0.016697687f, 0.017782797f,
            0.018938423f, 0.020169149f, 0.021479854f, 0.022875735f,
            0.024362330f, 0.025945531f, 0.027631618f, 0.029427276f,
            0.031339626f, 0.033376252f, 0.035545228f, 0.037855157f,
            0.040315199f, 0.042935108f, 0.045725273f, 0.048696758f,
            0.051861348f, 0.055231591f, 0.058820850f, 0.062643361f,
            0.066714279f, 0.071049749f, 0.075666962f, 0.080584227f,
            0.085821044f, 0.091398179f, 0.097337747f, 0.10366330f,
            0.11039993f, 0.11757434f, 0.12521498f, 0.13335215f,
            0.14201813f, 0.15124727f, 0.16107617f, 0.17154380f,
            0.18269168f, 0.19456402f, 0.20720788f, 0.22067342f,
            0.23501402f, 0.25028656f, 0.26655159f, 0.28387361f,
            0.30232132f, 0.32196786f, 0.34289114f, 0.36517414f,
            0.38890521f, 0.41417847f, 0.44109412f, 0.46975890f,
            0.50028648f, 0.53279791f, 0.56742212f, 0.60429640f,
            0.64356699f, 0.68538959f, 0.72993007f, 0.77736504f,
            0.82788260f, 0.88168307f, 0.9389798f, 1.0f,
    ];
}

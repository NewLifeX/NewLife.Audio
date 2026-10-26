using NewLife;

namespace NewLife.Audio.Codecs;

/// <summary>Vorbis 声道映射。包含子映射、声道耦合与复用</summary>
internal sealed class VorbisMapping
{
    private Int32[] _couplingAngle = [];
    private Int32[] _couplingMagnitude = [];
    private VorbisFloor[] _submapFloor = [];
    private VorbisResidue[] _submapResidue = [];
    private VorbisFloor[] _channelFloor = [];
    private VorbisResidue[] _channelResidue = [];

    /// <summary>读取映射配置</summary>
    /// <param name="reader">位流读取器</param>
    /// <param name="channels">声道数</param>
    /// <param name="floors">地板集合</param>
    /// <param name="residues">残差集合</param>
    /// <returns>映射实例</returns>
    public static VorbisMapping Read(ref VorbisBitReader reader, Int32 channels, VorbisFloor[] floors, VorbisResidue[] residues)
    {
        var mappingType = (Int32)reader.ReadBits(16);
        if (mappingType != 0) throw new InvalidDataException(L.T("The Vorbis mapping type is not supported."));

        var submapCount = 1;
        if (reader.ReadBit() != 0) submapCount += (Int32)reader.ReadBits(4);

        var couplingSteps = 0;
        if (reader.ReadBit() != 0) couplingSteps = (Int32)reader.ReadBits(8) + 1;

        var couplingBits = VorbisCodebook.ILog(channels - 1);
        var couplingAngle = new Int32[couplingSteps];
        var couplingMagnitude = new Int32[couplingSteps];
        for (var i = 0; i < couplingSteps; i++)
        {
            var magnitude = (Int32)reader.ReadBits(couplingBits);
            var angle = (Int32)reader.ReadBits(couplingBits);
            if (magnitude == angle || magnitude >= channels || angle >= channels)
                throw new InvalidDataException(L.T("The Vorbis channel coupling is invalid."));

            couplingMagnitude[i] = magnitude;
            couplingAngle[i] = angle;
        }

        if (reader.ReadBits(2) != 0) throw new InvalidDataException(L.T("The Vorbis mapping has reserved bits set."));

        var mux = new Int32[channels];
        if (submapCount > 1)
        {
            for (var i = 0; i < channels; i++)
            {
                mux[i] = (Int32)reader.ReadBits(4);
                if (mux[i] >= submapCount) throw new InvalidDataException(L.T("The Vorbis channel mux index is invalid."));
            }
        }

        var submapFloor = new VorbisFloor[submapCount];
        var submapResidue = new VorbisResidue[submapCount];
        for (var i = 0; i < submapCount; i++)
        {
            reader.SkipBits(8);
            var floorIndex = (Int32)reader.ReadBits(8);
            var residueIndex = (Int32)reader.ReadBits(8);
            if (floorIndex >= floors.Length || residueIndex >= residues.Length)
                throw new InvalidDataException(L.T("The Vorbis submap index is invalid."));

            submapFloor[i] = floors[floorIndex];
            submapResidue[i] = residues[residueIndex];
        }

        var channelFloor = new VorbisFloor[channels];
        var channelResidue = new VorbisResidue[channels];
        for (var i = 0; i < channels; i++)
        {
            channelFloor[i] = submapFloor[mux[i]];
            channelResidue[i] = submapResidue[mux[i]];
        }

        return new VorbisMapping
        {
            _couplingAngle = couplingAngle,
            _couplingMagnitude = couplingMagnitude,
            _submapFloor = submapFloor,
            _submapResidue = submapResidue,
            _channelFloor = channelFloor,
            _channelResidue = channelResidue,
        };
    }

    /// <summary>解码一个音频包的 floor/residue/耦合，并执行 IMDCT</summary>
    /// <param name="reader">位流读取器</param>
    /// <param name="blockSize">块大小</param>
    /// <param name="buffer">各声道频谱/时域缓冲</param>
    /// <param name="mdct">IMDCT 实例</param>
    public void DecodePacket(ref VorbisBitReader reader, Int32 blockSize, Single[][] buffer, VorbisMdct mdct)
    {
        var halfBlockSize = blockSize >> 1;
        var floorData = new VorbisFloorData[_channelFloor.Length];
        var noExecuteChannel = new Boolean[_channelFloor.Length];

        for (var i = 0; i < _channelFloor.Length; i++)
        {
            floorData[i] = _channelFloor[i].Unpack(ref reader, blockSize, i);
            noExecuteChannel[i] = !floorData[i].ExecuteChannel;

            Array.Clear(buffer[i], 0, halfBlockSize);
        }

        for (var i = 0; i < _couplingAngle.Length; i++)
        {
            if (floorData[_couplingAngle[i]].ExecuteChannel || floorData[_couplingMagnitude[i]].ExecuteChannel)
            {
                floorData[_couplingAngle[i]].ForceEnergy = true;
                floorData[_couplingMagnitude[i]].ForceEnergy = true;
            }
        }

        for (var i = 0; i < _submapFloor.Length; i++)
        {
            for (var j = 0; j < _channelFloor.Length; j++)
            {
                if (!ReferenceEquals(_submapFloor[i], _channelFloor[j]) || !ReferenceEquals(_submapResidue[i], _channelResidue[j]))
                    floorData[j].ForceNoEnergy = true;
            }

            _submapResidue[i].Decode(ref reader, noExecuteChannel, blockSize, buffer);
        }

        for (var i = _couplingAngle.Length - 1; i >= 0; i--)
        {
            if (!floorData[_couplingAngle[i]].ExecuteChannel && !floorData[_couplingMagnitude[i]].ExecuteChannel) continue;

            var magnitude = buffer[_couplingMagnitude[i]];
            var angle = buffer[_couplingAngle[i]];
            for (var j = 0; j < halfBlockSize; j++)
            {
                var oldM = magnitude[j];
                var oldA = angle[j];
                Single newM;
                Single newA;

                if (oldM > 0)
                {
                    if (oldA > 0)
                    {
                        newM = oldM;
                        newA = oldM - oldA;
                    }
                    else
                    {
                        newA = oldM;
                        newM = oldM + oldA;
                    }
                }
                else
                {
                    if (oldA > 0)
                    {
                        newM = oldM;
                        newA = oldM + oldA;
                    }
                    else
                    {
                        newA = oldM;
                        newM = oldM - oldA;
                    }
                }

                magnitude[j] = newM;
                angle[j] = newA;
            }
        }

        for (var i = 0; i < _channelFloor.Length; i++)
        {
            if (floorData[i].ExecuteChannel)
            {
                _channelFloor[i].Apply(floorData[i], blockSize, buffer[i]);
                mdct.Inverse(buffer[i], blockSize);
            }
            else
            {
                Array.Clear(buffer[i], halfBlockSize, halfBlockSize);
            }
        }
    }
}

using SharpJaad.AAC.Error;
using SharpJaad.AAC.Gain;
using SharpJaad.AAC.Huffman;
using SharpJaad.AAC.Tools;
using System;

namespace SharpJaad.AAC.Syntax
{
    public class ICStream
    {
        private const int SF_DELTA = 60;
        private const int SF_OFFSET = 200;

        /// <summary>PNS 噪声序列的默认种子</summary>
        public const Int32 DefaultNoiseSeed = 0x1F2E3D4C;

        private int _frameLength;
        //always needed
        private ICSInfo _info;
        private int[] _sfbCB;
        private int[] _sectEnd;
        private float[] _data;
        private Int32[] _quant;
        private float[] _scaleFactors;
        private int _globalGain;
        private bool _pulseDataPresent, _tnsDataPresent, _gainControlPresent;
        private Int32 _randomState;
        //only allocated if needed
        private TNS _tns;
        private GainControl _gainControl;
        private int[] _pulseOffset, _pulseAmp;
        private int _pulseCount;
        private int _pulseStartSWB;
        //error resilience
        private bool _noiseUsed;
        private int _reorderedSpectralDataLen, _longestCodewordLen;
        private RVLC _rvlc;

        /// <summary>用默认噪声种子构造</summary>
        /// <param name="config">解码器配置</param>
        public ICStream(DecoderConfig config) : this(config, DefaultNoiseSeed) { }

        /// <summary>指定 PNS 噪声种子构造</summary>
        /// <param name="config">解码器配置</param>
        /// <param name="noiseSeed">PNS 噪声序列种子；不同声道应使用不同种子，同一码流重复解码结果可复现</param>
        public ICStream(DecoderConfig config, Int32 noiseSeed)
        {
            _frameLength = config.GetFrameLength();
            _info = new ICSInfo(config);
            _sfbCB = new int[Constants.MAX_SECTIONS];
            _sectEnd = new int[Constants.MAX_SECTIONS];
            _data = new float[_frameLength];
            _quant = new Int32[_frameLength];
            _scaleFactors = new float[Constants.MAX_SECTIONS];
            _randomState = noiseSeed;
        }

        /* ========= decoding ========== */
        public void Decode(BitStream input, bool commonWindow, DecoderConfig conf)
        {
            if (conf.IsScalefactorResilienceUsed() && _rvlc == null) _rvlc = new RVLC();
            bool er = conf.GetProfile().IsErrorResilientProfile();

            _globalGain = input.ReadBits(8);

            if (!commonWindow) _info.Decode(input, conf, commonWindow);

            DecodeSectionData(input, conf.IsSectionDataResilienceUsed());

            //if(conf.isScalefactorResilienceUsed()) rvlc.decode(in, this, scaleFactors);
            /*else*/
            DecodeScaleFactors(input);

            _pulseDataPresent = input.ReadBool();
            if (_pulseDataPresent)
            {
                if (_info.IsEightShortFrame()) throw new AACException("pulse data not allowed for short frames");
                //LOGGER.log(Level.FINE, "PULSE");
                DecodePulseData(input);
            }

            _tnsDataPresent = input.ReadBool();
            if (_tnsDataPresent && !er)
            {
                if (_tns == null) _tns = new TNS();
                _tns.Decode(input, _info);
            }

            _gainControlPresent = input.ReadBool();
            if (_gainControlPresent)
            {
                if (_gainControl == null) _gainControl = new GainControl(_frameLength);
                //LOGGER.log(Level.FINE, "GAIN");
                _gainControl.Decode(input, _info.GetWindowSequence());
            }

            //RVLC spectral data
            //if(conf.isScalefactorResilienceUsed()) rvlc.decodeScalefactors(this, in, scaleFactors);

            if (conf.IsSpectralDataResilienceUsed())
            {
                int max = conf.GetChannelConfiguration() == ChannelConfiguration.CHANNEL_CONFIG_STEREO ? 6144 : 12288;
                _reorderedSpectralDataLen = Math.Max(input.ReadBits(14), max);
                _longestCodewordLen = Math.Max(input.ReadBits(6), 49);
                //HCR.decodeReorderedSpectralData(this, in, data, conf.isSectionDataResilienceUsed());
            }
            else DecodeSpectralData(input);
        }

        public void DecodeSectionData(BitStream input, bool sectionDataResilienceUsed)
        {
            Arrays.Fill(_sfbCB, 0);
            Arrays.Fill(_sectEnd, 0);
            int bits = _info.IsEightShortFrame() ? 3 : 5;
            int escVal = (1 << bits) - 1;

            int windowGroupCount = _info.GetWindowGroupCount();
            int maxSFB = _info.GetMaxSFB();

            int end, cb, incr;
            int idx = 0;

            for (int g = 0; g < windowGroupCount; g++)
            {
                int k = 0;
                while (k < maxSFB)
                {
                    end = k;
                    cb = input.ReadBits(4);
                    if (cb == 12) throw new AACException("invalid huffman codebook: 12");
                    while ((incr = input.ReadBits(bits)) == escVal)
                    {
                        end += incr;
                    }
                    end += incr;
                    if (end > maxSFB) throw new AACException("too many bands: " + end + ", allowed: " + maxSFB);
                    for (; k < end; k++)
                    {
                        _sfbCB[idx] = cb;
                        _sectEnd[idx++] = end;
                    }
                }
            }
        }

        private void DecodePulseData(BitStream input)
        {
            _pulseCount = input.ReadBits(2) + 1;
            _pulseStartSWB = input.ReadBits(6);
            if (_pulseStartSWB >= _info.GetSWBCount()) throw new AACException("pulse SWB out of range: " + _pulseStartSWB + " > " + _info.GetSWBCount());

            if (_pulseOffset == null || _pulseCount != _pulseOffset.Length)
            {
                //only reallocate if needed
                _pulseOffset = new int[_pulseCount];
                _pulseAmp = new int[_pulseCount];
            }

            _pulseOffset[0] = _info.GetSWBOffsets()[_pulseStartSWB];
            _pulseOffset[0] += input.ReadBits(5);
            _pulseAmp[0] = input.ReadBits(4);
            for (int i = 1; i < _pulseCount; i++)
            {
                _pulseOffset[i] = input.ReadBits(5) + _pulseOffset[i - 1];
                if (_pulseOffset[i] > 1023) throw new AACException("pulse offset out of range: " + _pulseOffset[0]);
                _pulseAmp[i] = input.ReadBits(4);
            }
        }

        public void DecodeScaleFactors(BitStream input)
        {
            int windowGroups = _info.GetWindowGroupCount();
            int maxSFB = _info.GetMaxSFB();
            //0: spectrum, 1: noise, 2: intensity
            int[] offset = { _globalGain, _globalGain - 90, 0 };

            int tmp;
            bool noiseFlag = true;

            int sfb, idx = 0;
            for (int g = 0; g < windowGroups; g++)
            {
                for (sfb = 0; sfb < maxSFB;)
                {
                    int end = _sectEnd[idx];
                    switch (_sfbCB[idx])
                    {
                        case HCB.ZERO_HCB:
                            for (; sfb < end; sfb++, idx++)
                            {
                                _scaleFactors[idx] = 0;
                            }
                            break;
                        case HCB.INTENSITY_HCB:
                        case HCB.INTENSITY_HCB2:
                            for (; sfb < end; sfb++, idx++)
                            {
                                offset[2] += HuffmanDec.DecodeScaleFactor(input) - SF_DELTA;
                                tmp = Math.Min(Math.Max(offset[2], -155), 100);
                                _scaleFactors[idx] = ScaleFactorTable.SCALEFACTOR_TABLE[-tmp + SF_OFFSET];
                            }
                            break;
                        case HCB.NOISE_HCB:
                            for (; sfb < end; sfb++, idx++)
                            {
                                if (noiseFlag)
                                {
                                    offset[1] += input.ReadBits(9) - 256;
                                    noiseFlag = false;
                                }
                                else offset[1] += HuffmanDec.DecodeScaleFactor(input) - SF_DELTA;
                                tmp = Math.Min(Math.Max(offset[1], -100), 155);
                                _scaleFactors[idx] = -ScaleFactorTable.SCALEFACTOR_TABLE[tmp + SF_OFFSET];
                            }
                            break;
                        default:
                            for (; sfb < end; sfb++, idx++)
                            {
                                offset[0] += HuffmanDec.DecodeScaleFactor(input) - SF_DELTA;
                                if (offset[0] > 255) throw new AACException("scalefactor out of range: " + offset[0]);
                                _scaleFactors[idx] = ScaleFactorTable.SCALEFACTOR_TABLE[offset[0] - 100 + SF_OFFSET];
                            }
                            break;
                    }
                }
            }
        }

        private void DecodeSpectralData(BitStream input)
        {
            Arrays.Fill(_data, 0);
            Arrays.Fill(_quant, 0);
            int maxSFB = _info.GetMaxSFB();
            int windowGroups = _info.GetWindowGroupCount();
            int[] offsets = _info.GetSWBOffsets();
            int[] buf = new int[4];

            int sfb, j, k, w, hcb, off, width, num;
            int groupOff = 0, idx = 0;
            for (int g = 0; g < windowGroups; g++)
            {
                int groupLen = _info.GetWindowGroupLength(g);

                for (sfb = 0; sfb < maxSFB; sfb++, idx++)
                {
                    hcb = _sfbCB[idx];
                    off = groupOff + offsets[sfb];
                    width = offsets[sfb + 1] - offsets[sfb];
                    if (hcb == HCB.ZERO_HCB || hcb == HCB.INTENSITY_HCB || hcb == HCB.INTENSITY_HCB2)
                    {
                        for (w = 0; w < groupLen; w++, off += 128)
                        {
                            Arrays.Fill(_data, off, off + width, 0);
                        }
                    }
                    else if (hcb == HCB.NOISE_HCB)
                    {
                        //apply PNS: fill with random values
                        _noiseUsed = true;
                        for (w = 0; w < groupLen; w++, off += 128)
                        {
                            float energy = 0;

                            for (k = 0; k < width; k++)
                            {
                                _randomState = 1664525 * _randomState + 1013904223;
                                _data[off + k] = _randomState;
                                energy += _data[off + k] * _data[off + k];
                            }

                            float scale = (float)(_scaleFactors[idx] / Math.Sqrt(energy));
                            for (k = 0; k < width; k++)
                            {
                                _data[off + k] *= scale;
                            }
                        }
                    }
                    else
                    {
                        for (w = 0; w < groupLen; w++, off += 128)
                        {
                            num = hcb >= HCB.FIRST_PAIR_HCB ? 2 : 4;
                            for (k = 0; k < width; k += num)
                            {
                                HuffmanDec.DecodeSpectralData(input, hcb, buf, 0);

                                //inverse quantization & scaling
                                for (j = 0; j < num; j++)
                                {
                                    _quant[off + k + j] = buf[j];
                                    _data[off + k + j] = buf[j] > 0 ? IQTable.IQ_TABLE[buf[j]] : -IQTable.IQ_TABLE[-buf[j]];
                                    _data[off + k + j] *= _scaleFactors[idx];
                                }
                            }
                        }
                    }
                }
                groupOff += groupLen << 7;
            }

            // 脉冲数据要加在"量化后、反量化前"的谱线上（ISO/IEC 14496-3 §4.6.3），加完再按所在缩放因子带重新反量化
            if (_pulseDataPresent) ApplyPulseData();
        }

        /// <summary>把脉冲幅度叠加到量化谱线上，并按所在缩放因子带重新反量化</summary>
        /// <remarks>
        /// 脉冲编码只用于长窗（短窗出现即按非法码流抛出），因此这里只有单窗口组，
        /// 缩放因子索引就是带序号。参考解码器（FAAD2 `pulse_decode`）只叠加幅度，符号沿用原系数的符号。
        /// </remarks>
        private void ApplyPulseData()
        {
            if (_pulseOffset == null || _pulseAmp == null || _pulseCount <= 0) return;

            var offsets = _info.GetSWBOffsets();
            var maxSFB = _info.GetMaxSFB();
            var length = _quant.Length;

            for (var i = 0; i < _pulseCount && i < _pulseOffset.Length; i++)
            {
                var k = _pulseOffset[i];
                if (k < 0 || k >= length) continue;

                var amp = _pulseAmp[i];
                if (_quant[k] > 0) _quant[k] += amp;
                else _quant[k] -= amp;

                // 找到该谱线所属的缩放因子带，用它的缩放因子重新反量化
                var sfb = -1;
                for (var s = 0; s < maxSFB && s + 1 < offsets.Length; s++)
                {
                    if (offsets[s] <= k && k < offsets[s + 1]) { sfb = s; break; }
                }
                if (sfb < 0) continue;

                var q = _quant[k];
                _data[k] = q > 0 ? IQTable.IQ_TABLE[q] : -IQTable.IQ_TABLE[-q];
                _data[k] *= _scaleFactors[sfb];
            }
        }

        /* =========== gets ============ */
        /**
		 * Does inverse quantization and applies the scale factors on the decoded
		 * data. After this the noiseless decoding is finished and the decoded data
		 * is returned.
		 * @return the inverse quantized and scaled data
		 */
        public float[] GetInvQuantData()
        {
            return _data;
        }

        public ICSInfo GetInfo()
        {
            return _info;
        }

        public int[] GetSectEnd()
        {
            return _sectEnd;
        }

        public int[] getSfbCB()
        {
            return _sfbCB;
        }

        public float[] GetScaleFactors()
        {
            return _scaleFactors;
        }

        public bool IsTNSDataPresent()
        {
            return _tnsDataPresent;
        }

        public TNS GetTNS()
        {
            return _tns;
        }

        public int GetGlobalGain()
        {
            return _globalGain;
        }

        public bool IsNoiseUsed()
        {
            return _noiseUsed;
        }

        public int GetLongestCodewordLength()
        {
            return _longestCodewordLen;
        }

        public int GetReorderedSpectralDataLength()
        {
            return _reorderedSpectralDataLen;
        }

        public bool IsGainControlPresent()
        {
            return _gainControlPresent;
        }

        public GainControl GetGainControl()
        {
            return _gainControl;
        }
    }
}

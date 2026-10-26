using SharpJaad.AAC.Syntax;

namespace SharpJaad.AAC.Tools
{
    public class TNS
    {
        private static int TNS_MAX_ORDER = 20;
        private static int[] SHORT_BITS = { 1, 4, 3 }, LONG_BITS = { 2, 6, 5 };
        //bitstream
        private int[] _nFilt;
        private int[,] _length, _order;
        private bool[,] _direction;
        private float[,,] _coef;

        public TNS()
        {
            _nFilt = new int[8];
            _length = new int[8, 4];
            _direction = new bool[8, 4];
            _order = new int[8, 4];
            _coef = new float[8, 4, TNS_MAX_ORDER];
        }

        public void Decode(BitStream input, ICSInfo info)
        {
            int windowCount = info.GetWindowCount();
            int[] bits = info.IsEightShortFrame() ? SHORT_BITS : LONG_BITS;

            int w, i, filt, coefLen, coefRes, coefCompress, tmp;
            for (w = 0; w < windowCount; w++)
            {
                if ((_nFilt[w] = input.ReadBits(bits[0])) != 0)
                {
                    coefRes = input.ReadBit();

                    for (filt = 0; filt < _nFilt[w]; filt++)
                    {
                        _length[w, filt] = input.ReadBits(bits[1]);

                        if ((_order[w, filt] = input.ReadBits(bits[2])) > 20) throw new AACException("TNS filter out of range: " + _order[w, filt]);
                        else if (_order[w, filt] != 0)
                        {
                            _direction[w, filt] = input.ReadBool();
                            coefCompress = input.ReadBit();
                            coefLen = coefRes + 3 - coefCompress;
                            tmp = 2 * coefCompress + coefRes;

                            for (i = 0; i < _order[w, filt]; i++)
                            {
                                _coef[w, filt, i] = TNSTables.TNS_TABLES[tmp][input.ReadBits(coefLen)];
                            }
                        }
                    }
                }
            }
        }

        /// <summary>对谱线应用 TNS 反向滤波</summary>
        /// <param name="ics">当前声道流</param>
        /// <param name="spec">逆量化后的谱线</param>
        /// <param name="sf">采样率（TNS 可覆盖的频带上限随之变化）</param>
        /// <param name="decode">true 表示 LTP 路径</param>
        /// <remarks>
        /// 尚未实现（移植自上游时即为空实现）：TNS 参数已被正确解析，但滤波器没生效。
        /// 后果用外部参考向量可量化——AAC-LC 前 4 秒与参考解码几乎逐样本一致（约 60 dB），
        /// 一旦码流开始用 TNS（本批向量约 3.7 秒起，order 6~12、覆盖 23~27 个缩放因子带），
        /// 整段 SNR 掉到 7~9 dB、频带能量比仍为 1.00，即频谱对、波形错。
        /// 实现要点见 ISO/IEC 14496-3 §4.6.9（反射系数→LPC、按 dir 位做全极点反向滤波、
        /// 频带范围从 num_swb 起按 length 向下计并受 max_tns_sfb 限制），已登记为待修复项。
        /// </remarks>
        public void Process(ICStream ics, float[] spec, SampleFrequency sf, bool decode)
        {
            // TODO TNS 反向滤波未实现，见功能清单待修复项
        }
    }
}

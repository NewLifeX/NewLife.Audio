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

        /// <summary>短窗 TNS 最大阶数</summary>
        private const Int32 MaxShortOrder = 7;

        /// <summary>对谱线应用 TNS 反向滤波</summary>
        /// <param name="ics">当前声道流</param>
        /// <param name="spec">逆量化后的谱线（长窗 1024 点，短窗 8×128 点）</param>
        /// <param name="sf">采样率（TNS 可覆盖的频带上限随之变化，本实现由 max_sfb 约束）</param>
        /// <param name="decode">true 表示 LTP 路径，语义不变</param>
        /// <remarks>
        /// 规范见 ISO/IEC 14496-3 §4.6.9：量化反射系数经 Levinson 递推转 LPC，再在
        /// 对应缩放因子带区间做全极点反向滤波，方向由方向位控制，每个滤波器状态清零。
        /// </remarks>
        public void Process(ICStream ics, float[] spec, SampleFrequency sf, bool decode)
        {
            if (ics == null || spec == null || spec.Length == 0) return;

            var info = ics.GetInfo();
            var windowCount = info.GetWindowCount();
            var offsets = info.GetSWBOffsets();
            var maxSfb = info.GetMaxSFB();
            var shortFrame = info.IsEightShortFrame();
            var frameLength = spec.Length;
            var windowLength = shortFrame ? frameLength / 8 : frameLength;
            var maxOrder = shortFrame ? MaxShortOrder : TNS_MAX_ORDER;

            for (var w = 0; w < windowCount && w < _nFilt.Length; w++)
            {
                // 滤波器从最高缩放因子带往下依次占用：length 以 sfB 为单位从"带总数"往下计，
                // 若从 max_sfb 起算会把本该保留的低频带一起滤掉。
                var bottom = info.GetSWBCount();

                for (var filt = 0; filt < _nFilt[w] && filt < 4; filt++)
                {
                    var top = bottom;
                    bottom = Math.Max(top - _length[w, filt], 0);

                    var order = Math.Min(_order[w, filt], maxOrder);
                    if (order <= 0) continue;

                    var start = Math.Min(bottom, maxSfb);
                    var end = Math.Min(top, maxSfb);
                    if (start >= end) continue;

                    var startIndex = Math.Min(offsets[start], windowLength);
                    var endIndex = Math.Min(offsets[end], windowLength);
                    var count = endIndex - startIndex;
                    if (count <= 0) continue;

                    var lpc = ConvertToLpc(w, filt, order);
                    var baseIndex = shortFrame ? w * windowLength : 0;
                    ArFilter(spec, baseIndex + startIndex, count, order, lpc, _direction[w, filt]);
                }
            }
        }

        /// <summary>反射系数转 LPC 系数（Levinson-Durbin 递推，a[0] 恒为 1）</summary>
        private Single[] ConvertToLpc(Int32 w, Int32 filt, Int32 order)
        {
            var lpc = new Single[order];
            var a = new Single[order + 1];
            var b = new Single[order + 1];
            a[0] = 1f;

            for (var m = 1; m <= order; m++)
            {
                // 本移植的系数表与 ISO 表符号相反，递推前先取反，才能与 AR 滤波的减号配对
                a[m] = -_coef[w, filt, m - 1];

                for (var i = 1; i < m; i++) b[i] = a[i] + a[m] * a[m - i];
                for (var i = 1; i < m; i++) a[i] = b[i];
            }

            for (var j = 0; j < order; j++) lpc[j] = a[j + 1];

            return lpc;
        }

        /// <summary>全极点反向滤波：y(n) = x(n) - Σ lpc[i]·y(n-1-i)</summary>
        private static void ArFilter(Single[] spec, Int32 start, Int32 length, Int32 order, Single[] lpc, Boolean reverse)
        {
            var state = new Single[order];
            var step = reverse ? -1 : 1;
            var index = reverse ? start + length - 1 : start;

            for (var n = 0; n < length; n++)
            {
                var y = spec[index];
                for (var i = 0; i < order; i++) y -= lpc[i] * state[i];

                for (var i = order - 1; i > 0; i--) state[i] = state[i - 1];
                state[0] = y;

                spec[index] = y;
                index += step;
            }
        }
    }
}

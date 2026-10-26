// Adapted from EggEncoder (MIT): src/EggEncoder/Transform/Mdct.cs
// https://github.com/eggspot/EggEncoder
using System.Buffers;
using System.Collections.Concurrent;

namespace NewLife.Audio.Codecs
{
    // The MDCT/IMDCT shared by the AAC, WMA and MP3 codecs, both scaled exactly as the direct
    // definitions (N = coefficient count, 2N samples):
    //   Forward: X[k] = sum_{n<2N} x[n] cos(pi/N (n + 1/2 + N/2)(k + 1/2))
    //   Inverse: y[n] = (1/N) sum_{k<N} X[k] cos(pi/N (n + 1/2 + N/2)(k + 1/2))
    //
    // A power-of-two N >= 4 (AAC's 1024/128, WMA's frame lengths) runs in O(N log N): the MDCT
    // folds its 2N inputs (quarters a, b, c, d) into the N-point DCT-IV input (-c_r - d, a - b_r),
    // and the IMDCT unfolds the DCT-IV output the same way; the DCT-IV itself is an N/2-point
    // complex FFT between a pre- and a post-twiddle. Results match the direct sums to within
    // floating-point rounding (relative error around 1e-12), not bit for bit. Any other N (MP3's
    // 18) uses the direct sums, with the cosines cached for small N, which keeps those results
    // bit-identical to the plain formula.
    internal static class AacMdct
    {
        // Direct-sum cosine tables are cached only up to this many entries (2N * N), so a caller
        // passing a large odd size doesn't pin a huge table in memory; MP3's N = 18 is 648.
        private const int MaxCachedDirectTableEntries = 1 << 16;

        private static readonly ConcurrentDictionary<int, FastPlan> _fastPlans = new();
        private static readonly ConcurrentDictionary<int, double[]> _directTables = new();

        public static double[] Forward(double[] samples)
        {
            var sampleCount = samples.Length;
            if (sampleCount % 2 != 0)
            {
                throw new ArgumentException("MDCT input length must be even", nameof(samples));
            }

            var coefficientCount = sampleCount / 2;
            var coefficients = new double[coefficientCount];

            if (IsFastSize(coefficientCount))
            {
                _fastPlans.GetOrAdd(coefficientCount, static n => new FastPlan(n)).Forward(samples, coefficients);
            }
            else
            {
                DirectForward(samples, coefficients);
            }

            return coefficients;
        }

        public static double[] Inverse(double[] coefficients)
        {
            var coefficientCount = coefficients.Length;
            var samples = new double[coefficientCount * 2];

            if (IsFastSize(coefficientCount))
            {
                _fastPlans.GetOrAdd(coefficientCount, static n => new FastPlan(n)).Inverse(coefficients, samples);
            }
            else
            {
                DirectInverse(coefficients, samples);
            }

            return samples;
        }

        private static bool IsFastSize(int coefficientCount) => coefficientCount >= 4 && (coefficientCount & (coefficientCount - 1)) == 0;

        private static void DirectForward(double[] samples, double[] coefficients)
        {
            var coefficientCount = coefficients.Length;
            var sampleCount = samples.Length;
            var table = GetDirectTable(coefficientCount);

            for (var k = 0; k < coefficientCount; k++)
            {
                var sum = 0.0;
                for (var n = 0; n < sampleCount; n++)
                {
                    sum += samples[n] * (table == null ? Kernel(coefficientCount, n, k) : table[(n * coefficientCount) + k]);
                }

                coefficients[k] = sum;
            }
        }

        private static void DirectInverse(double[] coefficients, double[] samples)
        {
            var coefficientCount = coefficients.Length;
            var sampleCount = samples.Length;
            var normalization = 1.0 / coefficientCount;
            var table = GetDirectTable(coefficientCount);

            for (var n = 0; n < sampleCount; n++)
            {
                var sum = 0.0;
                for (var k = 0; k < coefficientCount; k++)
                {
                    sum += coefficients[k] * (table == null ? Kernel(coefficientCount, n, k) : table[(n * coefficientCount) + k]);
                }

                samples[n] = sum * normalization;
            }
        }

        // The table holds Kernel(N, n, k) at [n * N + k], computed by the very same expression, so a
        // cached and an uncached direct sum produce identical bits.
        private static double[] GetDirectTable(int coefficientCount)
        {
            if (coefficientCount == 0 || 2L * coefficientCount * coefficientCount > MaxCachedDirectTableEntries)
            {
                return null;
            }

            return _directTables.GetOrAdd(coefficientCount, static count =>
            {
                var table = new double[2 * count * count];
                for (var n = 0; n < 2 * count; n++)
                {
                    for (var k = 0; k < count; k++)
                    {
                        table[(n * count) + k] = Kernel(count, n, k);
                    }
                }

                return table;
            });
        }

        private static double Kernel(int coefficientCount, int n, int k) =>
            Math.Cos((Math.PI / coefficientCount) * (n + 0.5 + (coefficientCount / 2.0)) * (k + 0.5));

        // Everything that depends only on N, built once per size and shared (read-only) by every
        // call and thread: the DCT-IV twiddles, the FFT's own twiddles and its bit-reversal order.
        private sealed class FastPlan
        {
            private readonly int _n;
            private readonly int _fftSize;
            private readonly double[] _preCos;
            private readonly double[] _preSin;
            private readonly double[] _postCos;
            private readonly double[] _postSin;
            private readonly double[] _fftCos;
            private readonly double[] _fftSin;
            private readonly int[] _bitReversed;

            public FastPlan(int n)
            {
                _n = n;
                _fftSize = n / 2;

                _preCos = new double[_fftSize];
                _preSin = new double[_fftSize];
                _postCos = new double[_fftSize];
                _postSin = new double[_fftSize];
                for (var i = 0; i < _fftSize; i++)
                {
                    var pre = -Math.PI * ((4 * i) + 1) / (4.0 * n);
                    _preCos[i] = Math.Cos(pre);
                    _preSin[i] = Math.Sin(pre);

                    var post = -Math.PI * i / n;
                    _postCos[i] = Math.Cos(post);
                    _postSin[i] = Math.Sin(post);
                }

                _fftCos = new double[_fftSize / 2];
                _fftSin = new double[_fftSize / 2];
                for (var i = 0; i < _fftSize / 2; i++)
                {
                    var angle = -2 * Math.PI * i / _fftSize;
                    _fftCos[i] = Math.Cos(angle);
                    _fftSin[i] = Math.Sin(angle);
                }

                _bitReversed = new int[_fftSize];
                var bits = 0;
                while ((1 << bits) < _fftSize) bits++;
                for (var i = 0; i < _fftSize; i++)
                {
                    _bitReversed[i] = (int)(ReverseBits((uint)i) >> (32 - bits));
                }
            }

            public void Forward(ReadOnlySpan<double> samples, Span<double> coefficients)
            {
                // Fold 2N samples (quarters a, b, c, d of N/2 each) into the DCT-IV input
                // (-c_r - d, a - b_r).
                var half = _n / 2;
                var folded = ArrayPool<double>.Shared.Rent(_n);
                try
                {
                    for (var m = 0; m < half; m++)
                    {
                        folded[m] = -samples[(3 * half) - 1 - m] - samples[m + (3 * half)];
                    }

                    for (var m = half; m < _n; m++)
                    {
                        folded[m] = samples[m - half] - samples[(3 * half) - 1 - m];
                    }

                    Dct4(folded.AsSpan(0, _n), coefficients);
                }
                finally
                {
                    ArrayPool<double>.Shared.Return(folded);
                }
            }

            public void Inverse(ReadOnlySpan<double> coefficients, Span<double> samples)
            {
                // Unfold the DCT-IV output y into 2N samples, scaled by 1/N: [n < N/2] = y[n + N/2],
                // [N/2 <= n < 3N/2] = -y[3N/2 - 1 - n], [n >= 3N/2] = -y[n - 3N/2].
                var half = _n / 2;
                var normalization = 1.0 / _n;
                var transformed = ArrayPool<double>.Shared.Rent(_n);
                try
                {
                    var y = transformed.AsSpan(0, _n);
                    Dct4(coefficients, y);

                    for (var n = 0; n < half; n++)
                    {
                        samples[n] = y[n + half] * normalization;
                    }

                    for (var n = half; n < 3 * half; n++)
                    {
                        samples[n] = -y[(3 * half) - 1 - n] * normalization;
                    }

                    for (var n = 3 * half; n < 2 * _n; n++)
                    {
                        samples[n] = -y[n - (3 * half)] * normalization;
                    }
                }
                finally
                {
                    ArrayPool<double>.Shared.Return(transformed);
                }
            }

            // Unnormalized DCT-IV, output[k] = sum_{n<N} input[n] cos(pi/N (n + 1/2)(k + 1/2)), via an
            // N/2-point complex FFT: pair input[2n] + i*input[N-1-2n], rotate by e^(-i*pi*(4n+1)/(4N)),
            // FFT, rotate by e^(-i*pi*k/N); the real parts are output[2k], the negated imaginary
            // parts output[N-1-2k]. input and output must not overlap.
            private void Dct4(ReadOnlySpan<double> input, Span<double> output)
            {
                var size = _fftSize;
                var buffer = ArrayPool<double>.Shared.Rent(2 * size);
                try
                {
                    var re = buffer.AsSpan(0, size);
                    var im = buffer.AsSpan(size, size);

                    // Pre-twiddle, stored straight into bit-reversed order for the in-place FFT.
                    for (var i = 0; i < size; i++)
                    {
                        var a = input[2 * i];
                        var b = input[_n - 1 - (2 * i)];
                        var target = _bitReversed[i];
                        re[target] = (a * _preCos[i]) - (b * _preSin[i]);
                        im[target] = (a * _preSin[i]) + (b * _preCos[i]);
                    }

                    // Iterative radix-2 decimation-in-time butterflies.
                    for (var length = 2; length <= size; length <<= 1)
                    {
                        var halfLength = length >> 1;
                        var twiddleStride = size / length;
                        for (var start = 0; start < size; start += length)
                        {
                            for (var j = 0; j < halfLength; j++)
                            {
                                var wr = _fftCos[j * twiddleStride];
                                var wi = _fftSin[j * twiddleStride];
                                var top = start + j;
                                var bottom = top + halfLength;
                                var vr = (re[bottom] * wr) - (im[bottom] * wi);
                                var vi = (re[bottom] * wi) + (im[bottom] * wr);
                                re[bottom] = re[top] - vr;
                                im[bottom] = im[top] - vi;
                                re[top] += vr;
                                im[top] += vi;
                            }
                        }
                    }

                    for (var k = 0; k < size; k++)
                    {
                        var real = (re[k] * _postCos[k]) - (im[k] * _postSin[k]);
                        var imaginary = (re[k] * _postSin[k]) + (im[k] * _postCos[k]);
                        output[2 * k] = real;
                        output[_n - 1 - (2 * k)] = -imaginary;
                    }
                }
                finally
                {
                    ArrayPool<double>.Shared.Return(buffer);
                }
            }

            private static uint ReverseBits(uint value)
            {
                var reversed = 0u;
                for (var bit = 0; bit < 32; bit++)
                {
                    reversed = (reversed << 1) | ((value >> bit) & 1);
                }

                return reversed;
            }
        }
    }
}

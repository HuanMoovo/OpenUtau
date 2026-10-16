using System;
using System.Linq;

using NumSharp;

using OpenUtau.Core.Render;

using Xunit;

namespace OpenUtau.Classic {
    /// <summary>
    /// Resample used to copy each of its segment's frames twice - once into a temporary with
    /// ToArray&lt;double&gt;() and again with Array.Copy into the padded sp/ap - and then walk the note
    /// output a second time for its peak with Enumerable.Max. It now reads the frames straight out
    /// of the NDArray backing memory, once per frame, and folds the peak into the float conversion
    /// loop. These tests freeze both of the old paths and compare the results element by element,
    /// bit for bit.
    /// </summary>
    public class WorldlineResampleFramesTest {
        // Worldline.ResamplerPadding, private: the padding the resampler runs with.
        const int ResamplerPadding = 2;

        /// <summary>
        /// A (frames, bins) matrix as SynthSegment builds one: allocated with np.ndarray and then
        /// written a row at a time. Every element is distinct, so a frame or bin taken from the
        /// wrong place shows up.
        /// </summary>
        static NDArray Matrix(int frames, int bins, double bias) {
            var matrix = np.ndarray(new Shape(frames, bins), typeof(double));
            for (int i = 0; i < frames; ++i) {
                var row = new double[bins];
                for (int j = 0; j < bins; ++j) {
                    row[j] = bias + i * 1000.0 + j * 0.25;
                }
                matrix[i] = np.array(row);
            }
            return matrix;
        }

        /// <summary>The f0 column of a segment.</summary>
        static NDArray Column(int frames, double bias) {
            var column = np.ndarray(new Shape(frames), typeof(double));
            for (int i = 0; i < frames; ++i) {
                column[i] = bias + i;
            }
            return column;
        }

        /// <summary>The old padding loop, frozen: three ToArray&lt;double&gt;() copies and an
        /// Array.Copy per matrix per frame.</summary>
        static (double[] f0, double[] sp, double[] ap) ToArrayPad(NDArray segF0, NDArray segSp, NDArray segAp,
            int spSize, int padding) {
            int length = segF0.size;
            int total = length + 2 * padding;
            double[] segF0Array = segF0.ToArray<double>();
            double[] segSpArray = segSp.ToArray<double>();
            double[] segApArray = segAp.ToArray<double>();
            var f0 = new double[total];
            var sp = new double[total * spSize];
            var ap = new double[total * spSize];
            for (int i = 0; i < total; ++i) {
                int src = Math.Clamp(i - padding, 0, length - 1);
                f0[i] = segF0Array[src];
                Array.Copy(segSpArray, src * spSize, sp, i * spSize, spSize);
                Array.Copy(segApArray, src * spSize, ap, i * spSize, spSize);
            }
            return (f0, sp, ap);
        }

        /// <summary>The old peak, frozen: Enumerable.Max over the float output.</summary>
        static float ToArrayMax(double[] samples, int startSample, int lengthSamples) {
            var output = new float[lengthSamples];
            for (int i = 0; i < lengthSamples; ++i) {
                output[i] = (float)samples[startSample + i];
            }
            return output.Length > 0 ? output.Max(s => Math.Abs(s)) : 0;
        }

        static void AssertBitEqual(double[] expected, double[] actual, string what) {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; ++i) {
                Assert.True(BitConverter.DoubleToInt64Bits(expected[i]) == BitConverter.DoubleToInt64Bits(actual[i]),
                    $"{what}[{i}]: ToArray {expected[i]:R} != backing memory {actual[i]:R}");
            }
        }

        static void AssertSameOutputAndPeak(double[] samples, int startSample, int lengthSamples) {
            var output = Worldline.ConvertOutput(samples, startSample, lengthSamples, out double peak);
            var expected = samples.Skip(startSample).Take(lengthSamples).Select(v => (float)v).ToArray();
            Assert.Equal(expected.Length, output.Length);
            for (int i = 0; i < expected.Length; ++i) {
                Assert.Equal(BitConverter.SingleToInt32Bits(expected[i]), BitConverter.SingleToInt32Bits(output[i]));
            }
            // The auto gain divides by this peak, so it has to stay bit-identical to Enumerable.Max.
            float expectedPeak = ToArrayMax(samples, startSample, lengthSamples);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expectedPeak), BitConverter.DoubleToInt64Bits(peak));
        }

        /// <summary>The frames read out of the backing memory are the buffer ToArray copies.</summary>
        [Theory]
        [InlineData(1, 3)]
        [InlineData(2, 5)]
        [InlineData(37, 1025)]
        [InlineData(100, 1025)]
        public void BackingMemoryIsTheRowMajorBufferToArrayCopies(int frames, int bins) {
            var matrix = Matrix(frames, bins, 0.5);
            var flat = Worldline.Frames(matrix, frames * bins);
            AssertBitEqual(matrix.ToArray<double>(), flat.ToArray(), "sp");
            var column = Column(frames, 100.0);
            AssertBitEqual(column.ToArray<double>(), Worldline.Frames(column, frames).ToArray(), "f0");
        }

        /// <summary>The padded frames are the ones the ToArray + Array.Copy loop produced, including
        /// both clamped edges.</summary>
        [Theory]
        [InlineData(1, 3, ResamplerPadding)]
        [InlineData(2, 5, ResamplerPadding)]
        [InlineData(37, 1025, ResamplerPadding)]
        [InlineData(5, 4, 0)]
        [InlineData(2, 3, 5)]
        public void PaddedFramesMatchTheToArrayCopyPath(int frames, int bins, int padding) {
            var segF0 = Column(frames, 100.0);
            var segSp = Matrix(frames, bins, 0.5);
            var segAp = Matrix(frames, bins, 0.25);
            var expected = ToArrayPad(segF0, segSp, segAp, bins, padding);
            int total = frames + 2 * padding;
            var f0 = new double[total];
            var sp = new double[total * bins];
            var ap = new double[total * bins];
            Worldline.PadResamplerFrames(segF0, segSp, segAp, bins, padding, f0, sp, ap);
            AssertBitEqual(expected.f0, f0, "f0");
            AssertBitEqual(expected.sp, sp, "sp");
            AssertBitEqual(expected.ap, ap, "ap");
        }

        [Fact]
        public void OutputAndPeakMatchTheSeparateMax() {
            var samples = Enumerable.Range(0, 64).Select(i => Math.Sin(i * 0.7) * 0.9).ToArray();
            AssertSameOutputAndPeak(samples, 0, samples.Length);
            // The output starts after the padding.
            AssertSameOutputAndPeak(samples, 3, samples.Length - 3);
            // Values above 1, with signs, over a shorter window.
            var loud = Enumerable.Range(0, 32).Select(i => (i % 2 == 0 ? 1 : -1) * (1.0 + i * 0.05)).ToArray();
            AssertSameOutputAndPeak(loud, 5, 20);
            // Nothing to keep: no samples, and a peak of 0.
            AssertSameOutputAndPeak(samples, samples.Length, 0);
        }

        /// <summary>Max seeds from the first element and folds with &gt;, so a NaN there propagates
        /// while a later NaN is skipped; the folded peak has to do the same.</summary>
        [Fact]
        public void PeakKeepsEnumerableMaxSemanticsOnNan() {
            var cases = new[] {
                new[] { double.NaN, 0.25, -0.5 },
                new[] { 0.25, double.NaN, -0.5 },
                new[] { 0.25, -0.5, double.NaN },
                new[] { double.NaN, double.NaN },
                new[] { double.PositiveInfinity, 1.0 },
            };
            foreach (var samples in cases) {
                var output = Worldline.ConvertOutput(samples, 0, samples.Length, out double peak);
                float expected = output.Max(s => Math.Abs(s));
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(peak));
            }
            var none = Worldline.ConvertOutput(Array.Empty<double>(), 0, 0, out double emptyPeak);
            Assert.Empty(none);
            Assert.Equal(0.0, emptyPeak);
        }
    }
}

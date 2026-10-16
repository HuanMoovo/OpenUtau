using System;
using System.Collections.Generic;
using System.Linq;

using NumSharp;

using Xunit;

namespace OpenUtau.Core.DiffSinger {
    /// <summary>
    /// PhraseSpeakerEmbedByFrame used to build its (frame, speaker) curve matrix row by row through
    /// NumSharp and standardize it one frame at a time with slice indexers. It now accumulates a flat
    /// row-major buffer and builds the NDArray once for the same np.dot, so the two have to agree bit
    /// for bit: the same per-curve additions in the same order, the same float sum (Enumerable.Sum of
    /// float[] accumulates in a double), the same division and the same remainder add on the default
    /// speaker. These tests run both and compare the raw bits, element by element.
    /// </summary>
    public class DiffSingerSpeakerEmbedCurveTest {
        // (speaker, per-frame curve values) pairs, the shape phrase.curves produces before the
        // voice color abbreviations and suffixes are resolved.
        readonly record struct Curve(int Speaker, float[] Values);

        /// <summary>The old row-wise path, frozen: NumSharp writes per curve, then one slice-indexed
        /// standardization per frame.</summary>
        static float[] RowWiseNumSharp(Curve[] curves, int totalFrames, int speakerCount, int[] defaultSpkByFrame) {
            NDArray spkCurves = np.zeros<float>(totalFrames, speakerCount);
            foreach (var curve in curves) {
                spkCurves[":", curve.Speaker] += curve.Values;
            }
            for (int frameId = 0; frameId < totalFrames; frameId++) {
                //standarization
                var spkSum = spkCurves[frameId, ":"].ToArray<float>().Sum();
                if (spkSum > 1) {
                    spkCurves[frameId, ":"] /= spkSum;
                } else {
                    spkCurves[frameId, defaultSpkByFrame[frameId]] += 1 - spkSum;
                }
            }
            return spkCurves.ToArray<float>();
        }

        /// <summary>The new path, as far as PhraseSpeakerEmbedByFrame drives it.</summary>
        static float[] FlatBuffer(Curve[] curves, int totalFrames, int speakerCount, int[] defaultSpkByFrame) {
            var spkCurves = new float[totalFrames * speakerCount];
            foreach (var curve in curves) {
                for (int frameId = 0; frameId < totalFrames; ++frameId) {
                    spkCurves[frameId * speakerCount + curve.Speaker] += curve.Values[frameId];
                }
            }
            DiffSingerSpeakerEmbedManager.NormalizeSpeakerCurves(spkCurves, totalFrames, speakerCount, defaultSpkByFrame);
            return spkCurves;
        }

        /// <summary>Per-frame sums of the raw curves, accumulated exactly like the standardization does.</summary>
        static float[] FrameSums(float[] spkCurves, int totalFrames, int speakerCount) {
            var sums = new float[totalFrames];
            for (int frameId = 0; frameId < totalFrames; ++frameId) {
                double sum = 0;
                for (int spkId = 0; spkId < speakerCount; ++spkId) {
                    sum += spkCurves[frameId * speakerCount + spkId];
                }
                sums[frameId] = (float)sum;
            }
            return sums;
        }

        /// <summary>The flat buffer as PhraseSpeakerEmbedByFrame accumulates it, before standardization.</summary>
        static float[] RawFlatBuffer(Curve[] curves, int totalFrames, int speakerCount) {
            var spkCurves = new float[totalFrames * speakerCount];
            foreach (var curve in curves) {
                for (int frameId = 0; frameId < totalFrames; ++frameId) {
                    spkCurves[frameId * speakerCount + curve.Speaker] += curve.Values[frameId];
                }
            }
            return spkCurves;
        }

        static void AssertBitEqual(float[] expected, float[] actual, string what) {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; ++i) {
                int expectedBits = BitConverter.SingleToInt32Bits(expected[i]);
                int actualBits = BitConverter.SingleToInt32Bits(actual[i]);
                Assert.True(expectedBits == actualBits,
                    $"{what}[{i}]: row-wise {expected[i]:R} (0x{expectedBits:X8}) != flat {actual[i]:R} (0x{actualBits:X8})");
            }
        }

        /// <summary>Runs both paths over the same curves and checks the whole matrix, including that
        /// the NDArray handed to np.dot reads the flat buffer row-major.</summary>
        static void AssertSameMatrix(Curve[] curves, int totalFrames, int speakerCount, int[] defaultSpkByFrame, bool expectBothBranches) {
            if (expectBothBranches) {
                var rawSums = FrameSums(RawFlatBuffer(curves, totalFrames, speakerCount), totalFrames, speakerCount);
                Assert.Contains(rawSums, s => s > 1);    // rescaled to 1
                Assert.Contains(rawSums, s => !(s > 1)); // remainder added to the default speaker
            }
            var expected = RowWiseNumSharp(curves, totalFrames, speakerCount, defaultSpkByFrame);
            var actual = FlatBuffer(curves, totalFrames, speakerCount, defaultSpkByFrame);
            AssertBitEqual(expected, actual, "spkCurves");
            // The matrix np.dot consumes is this buffer, read row-major:
            // flat[frame * speakerCount + spk] == matrix[frame, spk].
            var matrix = DiffSingerSpeakerEmbedManager.SpeakerCurvesToMatrix(actual, totalFrames, speakerCount);
            Assert.Equal(typeof(float), matrix.dtype);
            var matrixFlat = matrix.ToArray<float>();
            Assert.Equal(actual.Length, matrixFlat.Length);
            AssertBitEqual(actual, matrixFlat, "matrix flattened");
            for (int frameId = 0; frameId < totalFrames; ++frameId) {
                for (int spkId = 0; spkId < speakerCount; ++spkId) {
                    Assert.Equal(
                        BitConverter.SingleToInt32Bits(actual[frameId * speakerCount + spkId]),
                        BitConverter.SingleToInt32Bits((float)matrix[frameId, spkId]));
                }
            }
        }

        [Fact]
        public void BothStandardizationBranchesMatchTheRowWisePath() {
            // Frames 0 and 3 have curves summing above 1, frames 1, 2, 5 sum below 1, and frame 4 sums
            // to exactly 1 (the "not greater than" branch, which adds nothing).
            var curves = new[] {
                new Curve(0, new[] { 0.6f, 0.2f, 0f, 1.5f, 0.75f, 0f }),
                new Curve(2, new[] { 0.7f, 0.3f, 0f, 2.5f, 0.25f, 0f }),
            };
            AssertSameMatrix(curves, 6, 3, new[] { 0, 2, 1, 0, 2, 1 }, expectBothBranches: true);
        }

        [Fact]
        public void ManyCurvesAndSpeakersMatchTheRowWisePath() {
            const int totalFrames = 12;
            const int speakerCount = 4;
            // Four voice color curves, two of them on the same speaker (0): the additions per element
            // have to land in the same order as the row-wise writes did. Even frames sum above 1,
            // odd frames stay below it.
            var curves = new[] {
                new Curve(0, Enumerable.Range(0, totalFrames).Select(f => f % 2 == 0 ? 0.5f : 0.1f).ToArray()),
                new Curve(1, Enumerable.Range(0, totalFrames).Select(f => f % 2 == 0 ? 0.5f : 0.1f).ToArray()),
                new Curve(2, Enumerable.Range(0, totalFrames).Select(f => f % 2 == 0 ? 0.5f : 0.1f).ToArray()),
                new Curve(0, Enumerable.Range(0, totalFrames).Select(f => f % 2 == 0 ? 0.5f : 0.1f).ToArray()),
                new Curve(3, Enumerable.Range(0, totalFrames).Select(f => f * 0.01f).ToArray()),
            };
            var defaults = Enumerable.Range(0, totalFrames).Select(f => f % speakerCount).ToArray();
            AssertSameMatrix(curves, totalFrames, speakerCount, defaults, expectBothBranches: true);
        }

        [Fact]
        public void SumAccumulationMatchesEnumerableSumBitForBit() {
            // 2^24 + 1 + 1 is the classic case where accumulating in float loses the ones and
            // accumulating in double keeps them: the old sum came from Enumerable.Sum(float[]),
            // which uses a double accumulator, so the standardization divides by 16777218, not 16777216.
            var curves = new[] {
                new Curve(0, new[] { 16777216f, 0.1f, 0.5f }),
                new Curve(1, new[] { 1f, 0.2f, 0.25f }),
                new Curve(2, new[] { 1f, 0.3f, 0.25f }),
            };
            var rawSums = FrameSums(RawFlatBuffer(curves, 3, 3), 3, 3);
            Assert.Equal(16777218f, rawSums[0]); // double accumulation, not (float)(2^24 + 1 + 1)
            Assert.True(rawSums[1] < 1);
            AssertSameMatrix(curves, 3, 3, new[] { 0, 1, 2 }, expectBothBranches: true);
        }

        [Fact]
        public void SingleCurveFramesMatchTheRowWisePath() {
            var curves = new[] {
                new Curve(1, new[] { 0.25f, 1.5f, 0f, 0.9999999f, 2f }),
            };
            AssertSameMatrix(curves, 5, 2, new[] { 0, 1, 0, 1, 1 }, expectBothBranches: true);
        }

        [Fact]
        public void NoCurvesGivesEveryFrameToOneDefaultSpeaker() {
            // No curves at all: every frame takes the remainder on its default speaker.
            var curves = Array.Empty<Curve>();
            var defaults = new[] { 0, 2, 1 };
            AssertSameMatrix(curves, 3, 3, defaults, expectBothBranches: false);
            var actual = FlatBuffer(curves, 3, 3, defaults);
            Assert.Equal(new[] { 1f, 0f, 0f, 0f, 0f, 1f, 0f, 1f, 0f }, actual);
        }

        [Fact]
        public void DegenerateShapesDoNotThrowWhereTheOldPathDidNot() {
            // totalFrames == 0: nothing to standardize, and an empty matrix for np.dot either way.
            DiffSingerSpeakerEmbedManager.NormalizeSpeakerCurves(Array.Empty<float>(), 0, 2, Array.Empty<int>());
            var emptyMatrix = DiffSingerSpeakerEmbedManager.SpeakerCurvesToMatrix(Array.Empty<float>(), 0, 2);
            Assert.Empty(emptyMatrix.ToArray<float>());
        }

        [Fact]
        public void NormalizeSpeakerCurvesKeepsFramesAtOrBelowOneUntouched() {
            var spkCurves = new[] { 0.25f, 0.25f, 2f, 4f };
            DiffSingerSpeakerEmbedManager.NormalizeSpeakerCurves(spkCurves, 2, 2, new[] { 0, 1 });
            // Frame 0 is below one: its default speaker gets the 0.5 that is missing. Frame 1 is above
            // one: both channels are divided by the sum.
            Assert.Equal(new[] { 0.75f, 0.25f, 2f / 6f, 4f / 6f }, spkCurves);
        }
    }
}

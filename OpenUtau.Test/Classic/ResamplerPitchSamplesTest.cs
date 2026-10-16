using Xunit;

namespace OpenUtau.Classic {
    /// <summary>
    /// The pitch-bend curve of a phoneme is one sample per 5 ticks, and its length is
    /// derived from project data (the phoneme's ms span and the tempo). Issue 2395: a
    /// degenerate span made the render thread allocate the full count and die with
    /// "Array dimensions exceeded supported range" (OutOfMemoryException in
    /// ResamplerItem). The count saturates now, so no project data can size a curve
    /// beyond what a renderer can use.
    /// </summary>
    public class ResamplerPitchSamplesTest {
        // 480 ticks per beat: 1000 ms at 120 bpm is 960 ticks, / 5 = 192 samples.
        [Fact]
        public void CountFollowsTicksPerFiveMillisecondsTest() {
            Assert.Equal(192, ResamplerItem.PitchSampleCount(120, 1000));
            Assert.Equal(384, ResamplerItem.PitchSampleCount(240, 1000));
            Assert.Equal(1, ResamplerItem.PitchSampleCount(120, 5));
        }

        [Theory]
        [InlineData(120, 0)]
        [InlineData(120, -1000)]
        [InlineData(0, 1000)]
        [InlineData(-120, 1000)]
        [InlineData(double.NaN, 1000)]
        [InlineData(120, double.NaN)]
        public void UnusableInputYieldsNoSamplesTest(double tempo, double pitchCountMs) {
            Assert.Equal(0, ResamplerItem.PitchSampleCount(tempo, pitchCountMs));
        }

        [Fact]
        public void AbsurdLengthSaturatesInsteadOfAllocatingTest() {
            // The reported failure: ~15 days of ms at a normal tempo is a multi-GB int[].
            Assert.Equal(ResamplerItem.MaxPitchSamples, ResamplerItem.PitchSampleCount(120, 1.34e9));
            Assert.Equal(ResamplerItem.MaxPitchSamples, ResamplerItem.PitchSampleCount(120, 1e12));
            // A non-finite span used to cast to a platform-defined int (int.MaxValue on
            // arm64, which is an OutOfMemoryException, int.MinValue on x64, which is 0).
            Assert.Equal(ResamplerItem.MaxPitchSamples, ResamplerItem.PitchSampleCount(120, double.PositiveInfinity));
            Assert.Equal(ResamplerItem.MaxPitchSamples, ResamplerItem.PitchSampleCount(double.PositiveInfinity, 1000));
            // A long note is still well under the cap: 1000 seconds at 120 bpm.
            Assert.True(ResamplerItem.PitchSampleCount(120, 1e6) < ResamplerItem.MaxPitchSamples);
        }
    }
}

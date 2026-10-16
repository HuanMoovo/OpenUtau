using System;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Core {
    /// <summary>
    /// Playback must survive the ms position a degenerate project produces. Issue 2391:
    /// "Failed to render. TimeSpan does not accept floating point Not-a-Number values."
    /// from PlaybackManager.StartPlayback, where the start position went into
    /// TimeSpan.FromMilliseconds. The position comes from the time axis, so both ends are
    /// pinned here: the axis keeps its conversions finite, and the transport clamps what
    /// reaches TimeSpan.
    /// </summary>
    public class PlaybackStartMsTest {
        static TimeAxis AxisWithProjectTempo(double bpm) {
            var project = new UProject();
            project.tempos[0].bpm = bpm;
            var axis = new TimeAxis();
            axis.BuildSegments(project);
            return axis;
        }

        [Theory]
        [InlineData(1e-320)] // denormal: msPerTick overflows to infinity
        [InlineData(1e-307)]
        [InlineData(5e-324)]
        public void DegenerateTempoCannotProduceANonFiniteStartMsTest(double bpm) {
            var axis = AxisWithProjectTempo(bpm);

            // msPerTick * 0 with an infinite msPerTick is NaN, so tick 0 used to convert
            // to NaN and the playback start then threw inside TimeSpan.FromMilliseconds.
            Assert.True(double.IsFinite(axis.TickPosToMsPos(0)), "tick 0 must be a finite ms");
            Assert.True(double.IsFinite(axis.TickPosToMsPos(480)), "beat 1 must be a finite ms");
            Assert.Equal(120, axis.GetBpmAtTick(0)); // falls back to the default tempo
            Assert.Equal(500, axis.TickPosToMsPos(480), 6);

            // The exact call StartPlayback makes.
            var start = TimeSpan.FromMilliseconds(PlaybackManager.SanitizeStartMs(axis.TickPosToMsPos(0)));
            Assert.Equal(TimeSpan.Zero, start);
        }

        [Fact]
        public void NormalStartPositionIsUnchangedTest() {
            var axis = AxisWithProjectTempo(120);
            double ms = axis.TickPosToMsPos(960); // half a beat at 120 bpm, 480 tpn
            Assert.Equal(1000, ms, 6);
            Assert.Equal(1000, PlaybackManager.SanitizeStartMs(ms), 6);
        }

        [Fact]
        public void NonFiniteAndOutOfRangeStartsAreClampedTest() {
            Assert.Equal(0, PlaybackManager.SanitizeStartMs(double.NaN));
            Assert.Equal(0, PlaybackManager.SanitizeStartMs(double.PositiveInfinity));
            Assert.Equal(0, PlaybackManager.SanitizeStartMs(double.NegativeInfinity));
            Assert.Equal(0, PlaybackManager.SanitizeStartMs(-5000));
            // An unrepresentable length is clamped to what a TimeSpan holds, so the
            // conversion below cannot overflow either.
            Assert.Equal(PlaybackManager.MaxTimeSpanMs, PlaybackManager.SanitizeStartMs(1e300));
            TimeSpan.FromMilliseconds(PlaybackManager.SanitizeStartMs(1e300));
        }
    }
}

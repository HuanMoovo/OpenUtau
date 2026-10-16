using System;
using System.IO;
using NAudio.Wave;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Core.Format {
    /// <summary>
    /// A wave part whose file has more than two channels (5.1, 7.1) kept the file's
    /// channel count while GetStereoSamples hands back at most two channels, so every
    /// consumer strided the samples by six: the part played at a third of its speed and
    /// its waveform was nonsense. The part has to record the layout the samples have.
    /// </summary>
    public class WaveChannelTest {
        static string WriteWave(int channels, int frames) {
            string path = Path.Combine(Path.GetTempPath(), $"OpenUtau-WaveChannelTest-{Guid.NewGuid():N}.wav");
            using (var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, channels))) {
                var buffer = new byte[frames * channels * 2];
                new Random(7).NextBytes(buffer);
                writer.Write(buffer, 0, buffer.Length);
            }
            return path;
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(6, 2)]
        [InlineData(8, 2)]
        public void DecodedLayoutChannelCountTest(int fileChannels, int expected) {
            Assert.Equal(expected, Wave.GetStereoSampleChannels(new WaveFormat(44100, 16, fileChannels)));
        }

        [Fact]
        public void MultichannelWavePartLoadsAsStereoTest() {
            const int frames = 100;
            string path = WriteWave(6, frames);
            try {
                var project = new UProject();
                var part = new UWavePart { trackNo = 0, FilePath = path, position = 0 };
                part.Load(project);
                part.Peaks.Wait();

                Assert.Equal(2, part.channels);
                Assert.NotNull(part.Samples);
                Assert.Equal(frames * 2, part.Samples.Length);
                // The peaks follow the same layout; a six-channel stride over stereo
                // data would give a frame count three times too small.
                Assert.Equal(2, part.Peaks.Result.Channels);
                Assert.Equal(frames, part.Peaks.Result.Frames);
            } finally {
                File.Delete(path);
            }
        }

        [Fact]
        public void MonoWavePartKeepsItsLayoutTest() {
            const int frames = 100;
            string path = WriteWave(1, frames);
            try {
                var project = new UProject();
                var part = new UWavePart { trackNo = 0, FilePath = path, position = 0 };
                part.Load(project);
                part.Peaks.Wait();

                Assert.Equal(1, part.channels);
                Assert.NotNull(part.Samples);
                Assert.Equal(frames, part.Samples.Length);
                Assert.Equal(frames, part.Peaks.Result.Frames);
            } finally {
                File.Delete(path);
            }
        }

        /// <summary>
        /// skip and trim are tick counts read from the project file. A negative one (a
        /// hand-edited or foreign project) used to make the trimmed span longer than the
        /// decoded pcm, and preparing playback then threw in Array.Copy — a "Failed to
        /// render" the user cannot act on. The counts are clamped to the pcm now.
        /// </summary>
        [Fact]
        public void OutOfRangeSkipAndTrimAreClampedTest() {
            const int frames = 100;
            string path = WriteWave(2, frames);
            try {
                var project = new UProject();
                var part = new UWavePart { trackNo = 0, FilePath = path, position = 480 };
                part.Load(project);
                part.Peaks.Wait();
                Assert.Equal(2, part.channels);

                part.trim = -4800;
                var (_, _, channels, trimmed) = part.GetTrimmedSamples(project);
                Assert.Equal(2, channels);
                Assert.Equal(frames * 2, trimmed.Length);

                part.trim = 0;
                part.skip = -4800;
                var (_, _, _, skipped) = part.GetTrimmedSamples(project);
                Assert.Equal(frames * 2, skipped.Length);

                part.skip = 48000; // past the whole file
                var (_, _, _, empty) = part.GetTrimmedSamples(project);
                Assert.Empty(empty);
            } finally {
                File.Delete(path);
            }
        }
    }
}

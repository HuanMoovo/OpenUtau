using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Xunit;

namespace OpenUtau.Classic {
    public class VoicebankFilesTest : IDisposable {
        readonly string dir;

        public VoicebankFilesTest() {
            dir = Path.Combine(Path.GetTempPath(), "OpenUtau-VoicebankFilesTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        public void Dispose() {
            try {
                Directory.Delete(dir, true);
            } catch (Exception) {
            }
        }

        string InDir(string name) => Path.Combine(dir, name);

        static IResampler Resampler(params string[] files) => new FakeResampler(files);

        /// <summary>
        /// The pre-memoization implementation, kept as the reference the memoized one must
        /// match: same tuples, same order, no file system involved.
        /// </summary>
        static List<Tuple<string, string>> MetaFilesReference(string source, string sourceTemp, IResampler resampler) {
            string ext = Path.GetExtension(source);
            string noExt = source.Substring(0, source.Length - ext.Length);
            string frqExt = ext.Replace('.', '_') + ".frq";
            string tempExt = Path.GetExtension(sourceTemp);
            string tempNoExt = sourceTemp.Substring(0, sourceTemp.Length - ext.Length);
            string tempFrqExt = tempExt.Replace('.', '_') + ".frq";
            var ResamplerFiles = new List<Tuple<string, string>>() {
                Tuple.Create(noExt + frqExt, tempNoExt + tempFrqExt),
                Tuple.Create(source + ".llsm", sourceTemp + ".llsm"),
                Tuple.Create(source + ".uspec", sourceTemp + ".uspec"),
                Tuple.Create(source + ".dio", sourceTemp + ".dio"),
                Tuple.Create(source + ".star", sourceTemp + ".star"),
                Tuple.Create(source + ".platinum", sourceTemp + ".platinum"),
                Tuple.Create(source + ".frc", sourceTemp + ".frc"),
                Tuple.Create(source + ".pmk", sourceTemp + ".pmk"),
                Tuple.Create(source + ".vs4ufrq", sourceTemp + ".vs4ufrq"),
                Tuple.Create(noExt + ".rudb", tempNoExt + ".rudb"),
                Tuple.Create(noExt + ".sc.npz", tempNoExt + ".sc.npz"),
                Tuple.Create(noExt + ".sc", tempNoExt + ".sc"),
                Tuple.Create(noExt + "_hnsep", tempNoExt + "_hnsep"),
                Tuple.Create(noExt + ".hifi.npz", tempNoExt + ".hifi.npz"),
            };
            var files = resampler?.Manifest?.files;
            if (files != null) {
                for (int i = 0; i < files.Length; i++) {
                    ResamplerFiles.Add(Tuple.Create(noExt + $"{files[i]}", tempNoExt + $"{files[i]}"));
                }
            }
            return ResamplerFiles;
        }

        [Theory]
        [InlineData("wav")]
        [InlineData("flac")]
        public void GetMetaFilesMatchesThePreMemoizationList(string extension) {
            string source = InDir($"a.{extension}");
            string temp = InDir($"src-1-2-3.{extension}");
            foreach (var files in new[] {
                    Array.Empty<string>(),
                    new[] { "_one.llsm" },
                    new[] { "_one.llsm", "_two.uspec", "_three.hnsep" } }) {
                var expected = MetaFilesReference(source, temp, Resampler(files));
                var actual = VoicebankFiles.Inst.GetMetaFiles(source, temp, Resampler(files));
                Assert.Equal(expected.Count, actual.Length);
                for (int i = 0; i < expected.Count; ++i) {
                    Assert.Equal(expected[i].Item1, actual[i].Item1);
                    Assert.Equal(expected[i].Item2, actual[i].Item2);
                }
            }
        }

        [Fact]
        public void GetMetaFilesIsMemoizedPerKeyAndPerManifest() {
            string source = InDir("a.wav");
            string temp = InDir("src-1-2-3.wav");
            // Manifest files are appended to the extension-less source name.
            string noExt = InDir("a");
            var first = VoicebankFiles.Inst.GetMetaFiles(source, temp, Resampler("_one.llsm"));
            var second = VoicebankFiles.Inst.GetMetaFiles(source, temp, Resampler("_one.llsm"));
            Assert.Same(first, second);
            Assert.Contains(first, t => t.Item1 == noExt + "_one.llsm");

            // The same pair staged for a resampler with other manifest files must not reuse
            // the first resampler's entries.
            var other = VoicebankFiles.Inst.GetMetaFiles(source, temp, Resampler("_other.frq"));
            Assert.NotSame(first, other);
            Assert.Contains(other, t => t.Item1 == noExt + "_other.frq");
            Assert.DoesNotContain(other, t => t.Item1 == noExt + "_one.llsm");
            Assert.DoesNotContain(first, t => t.Item1 == noExt + "_other.frq");
        }

        [Fact]
        public void CopySourceTempStagesTheWavAndTheMetaFiles() {
            string source = InDir("a.wav");
            string temp = InDir("src-1-2-3.wav");
            File.WriteAllText(source, "wav");
            File.WriteAllText(InDir("a_wav.frq"), "frq");
            File.WriteAllText(source + ".llsm", "llsm");

            VoicebankFiles.Inst.CopySourceTemp(source, temp, Resampler());

            Assert.Equal("wav", File.ReadAllText(temp));
            Assert.Equal("frq", File.ReadAllText(InDir("src-1-2-3_wav.frq")));
            Assert.Equal("llsm", File.ReadAllText(temp + ".llsm"));
        }

        [Fact]
        public void CopyBackMetaFilesPicksUpMetadataProducedAfterStaging() {
            string source = InDir("b.wav");
            string temp = InDir("src-4-5-6.wav");
            File.WriteAllText(source, "wav");
            File.WriteAllText(InDir("b_wav.frq"), "frq");
            VoicebankFiles.Inst.CopySourceTemp(source, temp, Resampler());
            // The resampler wrote .uspec next to the staged wav after staging ran, and the
            // voicebank's frq went missing in the meantime.
            File.WriteAllText(temp + ".uspec", "resampler output");
            File.Delete(InDir("b_wav.frq"));

            VoicebankFiles.Inst.CopyBackMetaFiles(source, temp, Resampler());

            Assert.Equal("frq", File.ReadAllText(InDir("b_wav.frq")));
            Assert.Equal("resampler output", File.ReadAllText(source + ".uspec"));
        }

        [Fact]
        public void CopyingBackWithoutAStagedWavDoesNothing() {
            // The pairing of tuples must stay (source meta, temp meta) in both directions.
            string source = InDir("c.wav");
            string temp = InDir("src-7-8-9.wav");
            File.WriteAllText(source, "wav");
            File.WriteAllText(source + ".dio", "source dio");

            VoicebankFiles.Inst.CopyBackMetaFiles(source, temp, Resampler());

            Assert.Equal("source dio", File.ReadAllText(source + ".dio"));
            Assert.False(File.Exists(temp + ".dio"));
        }

        [Fact]
        public async Task ConcurrentStagingOfOnePairIsSerialized() {
            string source = InDir("d.wav");
            string temp = InDir("src-9-9-9.wav");
            File.WriteAllText(source, "wav");
            File.WriteAllText(InDir("d_wav.frq"), "frq");

            var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() => {
                if (i % 2 == 0) {
                    VoicebankFiles.Inst.CopySourceTemp(source, temp, Resampler());
                } else {
                    VoicebankFiles.Inst.CopyBackMetaFiles(source, temp, Resampler());
                }
            })).ToArray();
            await Task.WhenAll(tasks);

            Assert.Equal("wav", File.ReadAllText(temp));
            Assert.Equal("frq", File.ReadAllText(InDir("d_wav.frq")));
            Assert.Equal("frq", File.ReadAllText(InDir("src-9-9-9_wav.frq")));
        }

        class FakeResampler : IResampler {
            public string FilePath => string.Empty;
            public bool NoWrapperScript => true;
            public ResamplerManifest Manifest { get; }

            public FakeResampler(string[] files) {
                Manifest = new ResamplerManifest { files = files };
            }

            public float[] DoResampler(ResamplerItem args, ILogger logger) => Array.Empty<float>();
            public string DoResamplerReturnsFile(ResamplerItem args, ILogger logger) => args.outputFile;
            public void CheckPermissions() { }
            public bool SupportsFlag(string abbr) => true;
            public override string ToString() => "fake";
        }
    }
}

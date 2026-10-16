using System.IO;
using System.Text;
using Xunit;

namespace OpenUtau.Classic {
    public class VoicebankLoaderTest {
        readonly ITestOutputHelper output;

        public VoicebankLoaderTest(ITestOutputHelper output) {
            this.output = output;
        }

        [Fact]
        public void OtoSetRoundTrip() {
            string text = @"a.wav=,,,,,
a.wav=- a,
a.wav=a R,500,,,
!@#$!@#$
aoieu.wav=- a,,,,,,,,,

aoieu.wav=a o,,
aoieu.wav=o i,,,
aoieu.wav=i e,,100,150,,,
aoieu.wav=e u,20,
aoieu.wav=u R,5,,33,44,,
".Replace("\r\n", "\n");
            string expected = @"a.wav=a,0,0,0,0,0
a.wav=- a,0,0,0,0,0
a.wav=a R,500,0,0,0,0
!@#$!@#$
aoieu.wav=- a,0,0,0,0,0

aoieu.wav=a o,0,0,0,0,0
aoieu.wav=o i,0,0,0,0,0
aoieu.wav=i e,0,100,150,0,0
aoieu.wav=e u,20,0,0,0,0
aoieu.wav=u R,5,0,33,44,0
".Replace("\r\n", "\n");

            using (MemoryStream stream = new MemoryStream(Encoding.ASCII.GetBytes(text))) {
                VoicebankLoader.IsTest = true;
                var otoSet = VoicebankLoader.ParseOtoSet(stream, "oto.ini", Encoding.ASCII);
                using (MemoryStream stream2 = new MemoryStream()) {
                    VoicebankLoader.WriteOtoSet(otoSet, stream2, Encoding.ASCII);
                    string actual = Encoding.ASCII.GetString(stream2.ToArray());
                    Assert.Equal(expected, actual);
                }
            }
        }

        /// <summary>
        /// Parses an oto.ini through the same entry point a voicebank load uses, in a scratch
        /// directory with no wav files, so only the filename aliases are added.
        /// </summary>
        static OtoSet ParseSetWithFilenameAlias(string text) {
            System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string dir = Path.Combine(Path.GetTempPath(), "OpenUtau-VoicebankLoaderTest-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "oto.ini");
            File.WriteAllText(file, text.Replace("\r\n", "\n"), new UTF8Encoding(false));
            VoicebankLoader.IsTest = true;
            try {
                return VoicebankLoader.ParseOtoSet(file, Encoding.UTF8, useFilenameAsAlias: true);
            } finally {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void AddFilenameAliasCopiesTheLowestOffsetOtoOfTheWav() {
            // The lower offset comes later in file order, so the reference must be found by
            // offset and not by position.
            var otoSet = ParseSetWithFilenameAlias(
                "a.wav=first,100,10,20,30,40\n" +
                "a.wav=second,50,11,21,31,41\n");

            Assert.Equal(3, otoSet.Otos.Count);
            var alias = otoSet.Otos[2];
            Assert.Equal("a", alias.Alias);
            Assert.Equal("a", alias.Phonetic);
            Assert.Equal("a.wav", alias.Wav);
            Assert.Equal(50, alias.Offset);
            Assert.Equal(11, alias.Consonant);
            Assert.Equal(21, alias.Cutoff);
            Assert.Equal(31, alias.Preutter);
            Assert.Equal(41, alias.Overlap);
            Assert.Equal(string.Empty, alias.Error);
            Assert.True(alias.IsValid);
            Assert.Same(otoSet.Otos[1].FileTrace, alias.FileTrace);
        }

        [Fact]
        public void AddFilenameAliasKeepsTheFirstOtoOnEqualOffsets() {
            var otoSet = ParseSetWithFilenameAlias(
                "b.wav=first,50,1,2,3,4\n" +
                "b.wav=second,50,5,6,7,8\n");

            Assert.Equal(3, otoSet.Otos.Count);
            var alias = otoSet.Otos[2];
            Assert.Equal("b", alias.Alias);
            Assert.Equal(50, alias.Offset);
            Assert.Equal(1, alias.Consonant);
            Assert.Equal(2, alias.Cutoff);
            Assert.Equal(3, alias.Preutter);
            Assert.Equal(4, alias.Overlap);
        }

        [Fact]
        public void AddFilenameAliasSkipsExistingNamesAndAppendsInWavOrder() {
            var otoSet = ParseSetWithFilenameAlias(
                "c.wav=x,20,2,3,4,5\n" +
                "d.wav=d,10,1,1,1,1\n" +
                "c.wav=y,30,6,7,8,9\n");

            Assert.Equal(4, otoSet.Otos.Count);
            Assert.Equal("x", otoSet.Otos[0].Alias);
            Assert.Equal("d", otoSet.Otos[1].Alias);
            Assert.Equal("y", otoSet.Otos[2].Alias);
            // "d" already exists as an alias, so only "c" is appended.
            var alias = otoSet.Otos[3];
            Assert.Equal("c", alias.Alias);
            Assert.Equal("c.wav", alias.Wav);
            Assert.Equal(20, alias.Offset);
            Assert.Equal(2, alias.Consonant);
            Assert.Equal(3, alias.Cutoff);
            Assert.Equal(4, alias.Preutter);
            Assert.Equal(5, alias.Overlap);
        }

        [Fact]
        public void AddFilenameAliasIgnoresOtosThatFailedToParse() {
            var otoSet = ParseSetWithFilenameAlias(
                "!@#$!@#$\n" +
                "e.wav=ee,40,1,2,3,4\n");

            Assert.Equal(3, otoSet.Otos.Count);
            Assert.False(otoSet.Otos[0].IsValid);
            var alias = otoSet.Otos[2];
            Assert.Equal("e", alias.Alias);
            Assert.Equal("e.wav", alias.Wav);
            Assert.Equal(40, alias.Offset);
            Assert.Equal(1, alias.Consonant);
            Assert.Equal(2, alias.Cutoff);
            Assert.Equal(3, alias.Preutter);
            Assert.Equal(4, alias.Overlap);
        }
    }
}

using System;
using System.IO;
using System.Text;
using OpenUtau.Core.Util;
using Xunit;

namespace OpenUtau.Classic {
    /// <summary>
    /// A bank whose character.yaml does not write "portrait:" used to get no portrait at all, so
    /// nothing showed behind the piano roll. The loader now resolves one from the conventional
    /// names in the bank folder (portrait.*, character.*, 立绘.*) and, as the last resort, from the
    /// bank's own icon (character.txt "image=", e.g. the avatar.jpg the installed DiffSinger banks
    /// ship). Banks with no usable image behave exactly as before.
    /// </summary>
    public class PortraitFallbackTest {
        // Only the header matters: the loader never opens the image.
        static readonly byte[] Image = { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };

        /// <summary>
        /// Writes a bank into a scratch folder, loads it through the same entry point a singer load
        /// uses, hands it to the test and deletes the folder.
        /// </summary>
        static void WithBank(string characterTxt, string characterYaml, string[] files, Action<Voicebank, string> assert) {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string dir = Path.Combine(Path.GetTempPath(), "OpenUtau-PortraitFallbackTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                File.WriteAllText(Path.Combine(dir, VoicebankLoader.kCharTxt), characterTxt, new UTF8Encoding(false));
                if (characterYaml != null) {
                    File.WriteAllText(Path.Combine(dir, VoicebankLoader.kCharYaml), characterYaml, new UTF8Encoding(false));
                }
                foreach (var file in files) {
                    File.WriteAllBytes(Path.Combine(dir, file), Image);
                }
                VoicebankLoader.IsTest = true;
                var bank = new Voicebank { File = Path.Combine(dir, VoicebankLoader.kCharTxt), BasePath = dir };
                VoicebankLoader.LoadInfo(bank, bank.File, bank.BasePath);
                assert(bank, dir);
            } finally {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void FallsBackToTheBanksOwnImage() {
            WithBank("name=Test\nimage=avatar.jpg\n", null, new[] { "avatar.jpg" }, (bank, dir) => {
                Assert.Equal("avatar.jpg", bank.Portrait);
                Assert.True(File.Exists(Path.Combine(dir, bank.Portrait)), "The portrait must point at a real file");
                // Opacity 0 would mask the portrait away entirely - what kept the fallback invisible.
                Assert.Equal(VoicebankConfig.DefaultPortraitOpacity, bank.PortraitOpacity);
                Assert.Equal(0, bank.PortraitHeight);
            });
        }

        [Fact]
        public void PortraitFileWinsOverTheBanksOwnImage() {
            WithBank("name=Test\nimage=avatar.jpg\n", null, new[] { "portrait.png", "avatar.jpg" }, (bank, dir) => {
                Assert.Equal("portrait.png", bank.Portrait);
                Assert.True(File.Exists(Path.Combine(dir, bank.Portrait)));
            });
        }

        [Fact]
        public void ExplicitConfigPortraitWinsOverEveryConventionalFile() {
            WithBank("name=Test\nimage=avatar.jpg\n",
                "portrait: custom.png\nportrait_opacity: 0.5\nportrait_height: 600\n",
                new[] { "custom.png", "portrait.png", "character.png", "立绘.png", "avatar.jpg" },
                (bank, dir) => {
                    Assert.Equal("custom.png", bank.Portrait);
                    Assert.True(File.Exists(Path.Combine(dir, bank.Portrait)));
                    // A declared portrait keeps its own settings, unchanged.
                    Assert.Equal(0.5f, bank.PortraitOpacity);
                    Assert.Equal(600, bank.PortraitHeight);
                });
        }

        [Fact]
        public void ConventionalNamesAreMatchedCaseInsensitively() {
            // The folder holds Portrait.PNG: the conventional name is found whatever its
            // casing is, and the result names a file that exists. The spelling may be the one
            // the file system reports, so it is compared case-insensitively too.
            WithBank("name=Test\nimage=avatar.jpg\n", null, new[] { "Portrait.PNG", "avatar.jpg" }, (bank, dir) => {
                Assert.Equal("portrait.png", bank.Portrait, ignoreCase: true);
                Assert.True(File.Exists(Path.Combine(dir, bank.Portrait)));
            });
        }

        [Fact]
        public void ConventionalNamesFollowTheirPrecedence() {
            // portrait.* beats character.* beats 立绘.*, whatever the other files are.
            WithBank("name=Test\n", null, new[] { "character.png", "立绘.jpg" }, (bank, _) => Assert.Equal("character.png", bank.Portrait));
            WithBank("name=Test\n", null, new[] { "portrait.jpg", "character.png", "立绘.png" }, (bank, _) => Assert.Equal("portrait.jpg", bank.Portrait));
            // Inside a name, .png beats .jpg beats .jpeg.
            WithBank("name=Test\n", null, new[] { "portrait.jpeg", "portrait.png" }, (bank, _) => Assert.Equal("portrait.png", bank.Portrait));
            WithBank("name=Test\n", null, new[] { "portrait.jpeg", "portrait.jpg" }, (bank, _) => Assert.Equal("portrait.jpg", bank.Portrait));
        }

        [Fact]
        public void UnsupportedFileTypesAreNotPortraits() {
            WithBank("name=Test\n", null, new[] { "portrait.txt", "character.gif", "立绘.psd" }, (bank, _) => {
                Assert.Null(bank.Portrait);
            });
        }

        [Fact]
        public void BankWithoutAnyUsableImageKeepsBehavingAsBefore() {
            WithBank("name=Test\n", null, new string[0], (bank, _) => {
                Assert.Null(bank.Portrait);
                Assert.Equal(0f, bank.PortraitOpacity);
            });
            // An image that is declared but missing is not invented either.
            WithBank("name=Test\nimage=avatar.jpg\n", null, new string[0], (bank, _) => {
                Assert.Null(bank.Portrait);
                Assert.Equal(0f, bank.PortraitOpacity);
            });
        }

        [Fact]
        public void PortraitSettingsOfTheBankApplyToAFallbackPortrait() {
            // The installed AIkie banks declare portrait_opacity and ship an avatar.jpg, no portrait.
            WithBank("name=Test\nimage=avatar.jpg\n", "portrait_opacity: 0.5\n", new[] { "avatar.jpg" }, (bank, _) => {
                Assert.Equal("avatar.jpg", bank.Portrait);
                Assert.Equal(0.5f, bank.PortraitOpacity);
            });
        }

        [Fact]
        public void SingerResolvesTheFallbackPortraitToARealFile() {
            WithBank("name=Test\nimage=avatar.jpg\n", null, new[] { "avatar.jpg" }, (bank, dir) => {
                using var singer = new ClassicSinger(bank);
                Assert.Equal(Path.Combine(dir, "avatar.jpg"), singer.Portrait);
                Assert.True(File.Exists(singer.Portrait));
                Assert.NotEqual(0f, singer.PortraitOpacity);
            });
        }

        [Fact]
        public void ArchiveEntriesFollowTheSameChain() {
            // vogen .vogeon banks name their portrait by archive key.
            Assert.Equal("img/portrait.png", PortraitFinder.FindInEntries(new[] { "img/portrait.png", "avatar.png" }, "avatar.png"));
            Assert.Equal("CHARACTER.JPG", PortraitFinder.FindInEntries(new[] { "CHARACTER.JPG" }, null));
            Assert.Equal("avatar.png", PortraitFinder.FindInEntries(new[] { "model.onnx", "avatar.png" }, "avatar.png"));
            Assert.Null(PortraitFinder.FindInEntries(new[] { "model.onnx" }, null));
            // A declared image that is not in the archive does not become a portrait.
            Assert.Null(PortraitFinder.FindInEntries(new[] { "model.onnx", "avatar.png" }, null));
        }
    }
}

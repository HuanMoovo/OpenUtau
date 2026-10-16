using System.Collections.Generic;
using Xunit;

namespace OpenUtau.Core.DiffSinger {
    /// <summary>
    /// The rhythmizer's phoneme lookup table replaces the per-note and per-phoneme scans
    /// of the phoneme list; the index it returns must be the one List.IndexOf returned.
    /// </summary>
    public class RhythmizerPhonemeIndexTest {
        [Fact]
        public void PhonemeIndexesMatchTheFirstIndexOfEachPhoneme() {
            var phonemes = new List<string> { "SP", "a", "i", "a", "b", "SP" };
            var indexes = DsRhythmizer.IndexPhonemes(phonemes);
            foreach (var phoneme in new[] { "SP", "a", "i", "b" }) {
                Assert.True(indexes.ContainsKey(phoneme));
                Assert.Equal(phonemes.IndexOf(phoneme), indexes[phoneme]);
            }
            Assert.False(indexes.ContainsKey("z"));
        }

        [Fact]
        public void MissingPhonemesHaveNoIndex() {
            var indexes = DsRhythmizer.IndexPhonemes(new string[0]);
            Assert.Empty(indexes);
            Assert.False(indexes.TryGetValue("SP", out _));
        }
    }
}

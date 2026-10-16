using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace OpenUtau.Core.DiffSinger {
    public class DiffSingerPhNumGapTest {
        /// <summary>
        /// The pre-change search, kept as the golden reference: for every gap between two
        /// phones, restart at k = 0 and take the first non-slur note whose position - ep is
        /// past the previous phone; -1 means no note matched and the increment goes to the
        /// trailing bucket.
        /// </summary>
        static void FixupGapPhNumsRestartFromZero(List<int> phNumList,
                int[] phonePositions, double[] phonePositionMs, double[] phoneEndMs,
                int[] nonSlurNotePositions, int ep) {
            for (int i = 1; i < phonePositions.Length; ++i) {
                if (phonePositionMs[i] <= phoneEndMs[i - 1]) {
                    continue;
                }
                int owner = -1;
                for (int k = 0; k < nonSlurNotePositions.Length; ++k) {
                    if (phonePositions[i - 1] < nonSlurNotePositions[k] - ep) {
                        owner = k;
                        break;
                    }
                }
                ++phNumList[owner < 0 ? nonSlurNotePositions.Length : owner];
            }
        }

        static List<int> Run(bool reference,
                int[] phonePositions, double[] phonePositionMs, double[] phoneEndMs,
                int[] nonSlurNotePositions, int ep) {
            // The gap fixup runs once ph_num already holds one entry per non-slur note plus
            // the head/tail buckets, which is the length the loop indexes into.
            var phNumList = Enumerable.Repeat(7, nonSlurNotePositions.Length + 2).ToList();
            if (reference) {
                FixupGapPhNumsRestartFromZero(phNumList,
                    phonePositions, phonePositionMs, phoneEndMs, nonSlurNotePositions, ep);
            } else {
                DiffSingerScript.FixupGapPhNums(phNumList,
                    phonePositions, phonePositionMs, phoneEndMs, nonSlurNotePositions, ep);
            }
            return phNumList;
        }

        [Fact]
        public void CursorSearchMatchesTheRestartFromZeroSearch() {
            var random = new Random(20261016);
            for (int iteration = 0; iteration < 5000; ++iteration) {
                int phoneCount = random.Next(0, 12);
                int noteCount = random.Next(0, 8);
                var phonePositions = new int[phoneCount];
                var phonePositionMs = new double[phoneCount];
                var phoneEndMs = new double[phoneCount];
                int position = random.Next(-20, 20);
                for (int i = 0; i < phoneCount; ++i) {
                    // Mostly forwards, with periodic steps back to cover the cursor rewind,
                    // and small coordinate ranges so ties on the - ep boundary show up.
                    position += random.Next(0, 6);
                    if (iteration % 3 == 0 && i % 3 == 2) {
                        position -= 4;
                    }
                    phonePositions[i] = position;
                    phonePositionMs[i] = position * 10 + random.Next(0, 20);
                    phoneEndMs[i] = phonePositionMs[i] + random.Next(0, 15);
                }
                var notePositions = new int[noteCount];
                int notePosition = random.Next(-20, 20);
                for (int i = 0; i < noteCount; ++i) {
                    notePosition += random.Next(0, 8);
                    notePositions[i] = notePosition;
                }
                int ep = random.Next(0, 3) == 0 ? 0 : 4;

                var expected = Run(true,
                    phonePositions, phonePositionMs, phoneEndMs, notePositions, ep);
                var actual = Run(false,
                    phonePositions, phonePositionMs, phoneEndMs, notePositions, ep);

                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public void GapPastEveryNoteGoesToTheTrailingBucket() {
            var phonePositions = new[] { 50, 100 };
            var phonePositionMs = new[] { 500.0, 1000.0 };
            var phoneEndMs = new[] { 510.0, 1010.0 };
            var notePositions = new[] { 0, 10 };

            var phNumList = Run(false,
                phonePositions, phonePositionMs, phoneEndMs, notePositions, 4);

            // Both notes sit before the first phone, so no note owns the gap: index
            // nonSlurNotePositions.Length takes it.
            Assert.Equal(new[] { 7, 7, 8, 7 }, phNumList);
        }

        [Fact]
        public void GapIsOwnedByTheFirstNotePastThePreviousPhone() {
            // The gap after the first phone (tick 50) is past note 0, so note 1 owns it; the
            // gap after the second phone (tick 250) is past notes 0 and 1, so note 2 owns it.
            var phonePositions = new[] { 50, 250, 350 };
            var phonePositionMs = new[] { 500.0, 2500.0, 3500.0 };
            var phoneEndMs = new[] { 510.0, 2510.0, 3510.0 };
            var notePositions = new[] { 0, 100, 300, 400 };

            var phNumList = Run(false,
                phonePositions, phonePositionMs, phoneEndMs, notePositions, 4);

            Assert.Equal(new[] { 7, 8, 8, 7, 7, 7 }, phNumList);
        }

        [Fact]
        public void NoGapBetweenOverlappingPhones() {
            // phones[i].positionMs <= phones[i - 1].endMs skips the phone: nothing to fix.
            var phonePositions = new[] { 0, 10, 20 };
            var phonePositionMs = new[] { 0.0, 10.0, 20.0 };
            var phoneEndMs = new[] { 100.0, 100.0, 100.0 };
            var notePositions = new[] { 0, 100 };

            var phNumList = Run(false,
                phonePositions, phonePositionMs, phoneEndMs, notePositions, 4);

            Assert.Equal(new[] { 7, 7, 7, 7 }, phNumList);
        }
    }
}

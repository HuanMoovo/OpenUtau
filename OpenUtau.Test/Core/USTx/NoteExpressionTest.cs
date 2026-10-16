using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace OpenUtau.Core.Ustx {
    /// <summary>
    /// The per-phoneme index loops behind the expression commands, run for every selected
    /// note of a drag. The reference loops below are the scans the methods used before the
    /// set and hoisted lookups; the results must stay identical.
    /// </summary>
    public class NoteExpressionTest {
        static (UProject project, UTrack track) Fixture() {
            var project = new UProject();
            project.RegisterExpression(new UExpressionDescriptor("gender", "gen", -100, 100, 0));
            return (project, project.tracks[0]);
        }

        static UNote Note(params int[] phonemeIndexes) {
            var note = UNote.Create();
            note.phonemeIndexes = phonemeIndexes;
            return note;
        }

        [Fact]
        public void NoteHasKeepsThePerIndexResults() {
            var (project, track) = Fixture();
            var note = Note(0, 2);
            note.phonemeExpressions.Add(new UExpression(project.expressions["gen"]) { index = 0, value = 10 });
            note.phonemeExpressions.Add(new UExpression(project.expressions["gen"]) { index = 2, value = 30 });

            Assert.Equal(ReferenceNoteHas(note), note.GetExpressionNoteHas(project, track, "gen"));
            // An expression of another kind is not read.
            Assert.All(note.GetExpressionNoteHas(project, track, "vel"), value => Assert.Null(value));

            // No phoneme indexes: nothing to read.
            Assert.Empty(Note().GetExpressionNoteHas(project, track, "gen"));
        }

        [Fact]
        public void SetExpressionKeepsThePerIndexResults() {
            var (project, track) = Fixture();

            // One value per index.
            var note = Note(0, 2);
            var values = new float?[] { 10, 20, 30 };
            note.SetExpression(project, track, "gen", values);
            Assert.Equal(ReferenceSet(note, values), Expressed(note));
            // A single value, as the "set expression on all notes" command passes it: the
            // indexes past the array take the last value.
            values = new float?[] { 7 };
            note.SetExpression(project, track, "gen", values);
            Assert.Equal(ReferenceSet(note, values), Expressed(note));
            // A null clears.
            note.SetExpression(project, track, "gen", new float?[] { null, null, null });
            Assert.Empty(note.phonemeExpressions);

            // Index 0 is written even when it is not listed among the phoneme indexes.
            var other = Note(1);
            var single = new float?[] { 5 };
            other.SetExpression(project, track, "gen", single);
            Assert.Equal(ReferenceSet(other, single), Expressed(other));
        }

        /// <summary>The values the note reports, by index, for the given expression.</summary>
        static float?[] ReferenceNoteHas(UNote note) {
            var list = new List<float?>();
            if (note.phonemeIndexes.Length > 0) {
                int indexes = note.phonemeIndexes.LastOrDefault() + 1;
                UExpression phonemeExp = null;
                for (int i = 0; i < indexes; i++) {
                    if (note.phonemeIndexes.Contains(i)) {
                        phonemeExp = note.phonemeExpressions
                            .FirstOrDefault(exp => exp.descriptor?.abbr == "gen" && exp.index == i);
                    }
                    list.Add(phonemeExp != null ? (float?)phonemeExp.value : null);
                }
            }
            return list.ToArray();
        }

        /// <summary>The expressions the note keeps, in index order.</summary>
        static List<(int?, float)> Expressed(UNote note) => note.phonemeExpressions
            .Select(exp => (exp.index, exp.value))
            .ToList();

        /// <summary>
        /// The expressions <see cref="UNote.SetExpression"/> leaves for a values array.
        /// </summary>
        static List<(int?, float)> ReferenceSet(UNote note, float?[] values) {
            var kept = note.phonemeExpressions
                .Where(exp => exp.descriptor?.abbr != "gen")
                .Select(exp => (exp.index, exp.value))
                .ToList();
            int indexes = note.phonemeIndexes.LastOrDefault() + 1;
            for (int i = 0; i < indexes; i++) {
                if (i == 0 || note.phonemeIndexes.Contains(i)) {
                    float? value = values.Length > i ? values[i] : values.Last();
                    if (value != null) {
                        kept.Add((i, (float)value));
                    }
                }
            }
            kept.Sort();
            return kept;
        }
    }
}

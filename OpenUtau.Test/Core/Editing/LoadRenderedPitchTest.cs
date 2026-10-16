using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Core.Pipeline;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Core.Editing {
    /// <summary>
    /// A rendered-pitch pass writes its samples as one batched curve command. The pass must
    /// still end on exactly the curve the per-sample commands produced (and the merge path
    /// must still have simplified it, when it merged before), the group must hold one curve
    /// command for the whole pass, and Undo/Redo must round-trip through it.
    /// </summary>
    [Collection(RenderSingletonCollection.Name)]
    public class LoadRenderedPitchTest : IDisposable {
        const string PITD = Format.Ustx.PITD;
        const string RPIT = Format.Ustx.RPIT;

        readonly UProject previousProject;
        readonly Thread previousMainThread;
        readonly Action<Action> previousPost;
        readonly Recorder recorder = new Recorder();

        public LoadRenderedPitchTest() {
            previousProject = DocManager.Inst.Project;
            previousMainThread = DocManager.Inst.TakeMainThreadForTest(Thread.CurrentThread);
            previousPost = DocManager.Inst.PostOnUIThread;
            DocManager.Inst.PostOnUIThread = action => action();
            DocManager.Inst.AddSubscriber(recorder);
        }

        public void Dispose() {
            DocManager.Inst.RemoveSubscriber(recorder);
            DocManager.Inst.PostOnUIThread = previousPost;
            DocManager.Inst.TakeMainThreadForTest(previousMainThread);
            DocManager.Inst.TakeProjectForTest(previousProject);
        }

        [Fact]
        public void PassWithoutRenderedPitchCommandMergesToOneCommand() {
            // No RPIT expression: the group is all curve commands, so it merges at EndUndoGroup
            // and the curve it leaves behind is the simplified one, as before.
            var (project, part, phrase) = Fixture(registerRpit: false);
            var result = Result();

            var original = part.curves.First(c => c.abbr == PITD).Clone();
            var expected = ExpectedWrites(project, part, phrase, result);

            new LoadRenderedPitch().Run(project, part, new List<UNote>(), DocManager.Inst);

            // One curve command for the whole pass, carrying exactly the pass's samples.
            var batch = Assert.IsType<SetCurveBatchCommand>(
                Assert.Single(recorder.PitchCurveCommands(isUndo: false)));
            Assert.Equal(expected, batch.points);
            Assert.NotEmpty(batch.points);

            // The same writes through one per-sample command each, merged by the group as
            // before, end on the same curve.
            var twin = ReplayOldFlow(project, part, original, expected, rpitCommand: false);
            var curve = part.curves.First(c => c.abbr == PITD);
            Assert.NotEqual(Snapshot(original), Snapshot(curve));
            Assert.Equal(Snapshot(twin), Snapshot(curve));

            // The merged group holds one command, and undo restores the curve before the pass.
            recorder.Commands.Clear();
            DocManager.Inst.Undo();
            Assert.IsType<MergedSetCurveCommand>(Assert.Single(recorder.PitchCurveCommands(isUndo: true)));
            Assert.Equal(Snapshot(original), Snapshot(curve));

            recorder.Commands.Clear();
            DocManager.Inst.Redo();
            Assert.IsType<MergedSetCurveCommand>(Assert.Single(recorder.PitchCurveCommands(isUndo: false)));
            Assert.Equal(Snapshot(twin), Snapshot(curve));
        }

        [Fact]
        public void PassWithRenderedPitchCommandKeepsOneCurveCommandInTheGroup() {
            // RPIT is registered, so the group ends with its command and does not merge: one
            // batched curve command where the per-sample commands left one per sample.
            var (project, part, phrase) = Fixture(registerRpit: true);
            var result = Result();

            var original = part.curves.First(c => c.abbr == PITD).Clone();
            var expected = ExpectedWrites(project, part, phrase, result);

            new LoadRenderedPitch().Run(project, part, new List<UNote>(), DocManager.Inst);

            var batch = Assert.IsType<SetCurveBatchCommand>(
                Assert.Single(recorder.PitchCurveCommands(isUndo: false)));
            Assert.Equal(expected, batch.points);
            // The masked curve's command is in the same group, after the pitch writes.
            var all = recorder.Commands.Where(c => !c.isUndo).Select(c => c.cmd).ToList();
            Assert.Contains(all, c => c is ReplaceMaskedCurveCommand);
            Assert.True(all.IndexOf(batch) < all.FindIndex(c => c is ReplaceMaskedCurveCommand));

            var twin = ReplayOldFlow(project, part, original, expected, rpitCommand: true);
            var curve = part.curves.First(c => c.abbr == PITD);
            Assert.Equal(Snapshot(twin), Snapshot(curve));

            recorder.Commands.Clear();
            DocManager.Inst.Undo();
            Assert.IsType<SetCurveBatchCommand>(Assert.Single(recorder.PitchCurveCommands(isUndo: true)));
            Assert.Equal(Snapshot(original), Snapshot(curve));

            recorder.Commands.Clear();
            DocManager.Inst.Redo();
            Assert.IsType<SetCurveBatchCommand>(Assert.Single(recorder.PitchCurveCommands(isUndo: false)));
            Assert.Equal(Snapshot(twin), Snapshot(curve));
        }

        [Fact]
        public void TransientPassAppliesTheSameCurve() {
            // The live path records no undo: it must apply the same writes and keep no snapshot.
            var (project, part, phrase) = Fixture(registerRpit: false);
            var result = Result();

            var original = part.curves.First(c => c.abbr == PITD).Clone();
            var expected = ExpectedWrites(project, part, phrase, result);

            new LoadRenderedPitch().RunLive(project, part, new List<UNote>(), DocManager.Inst,
                CancellationToken.None, pitchSteps: 5, fastRealtime: true);

            var batch = Assert.IsType<SetCurveBatchCommand>(
                Assert.Single(recorder.PitchCurveCommands(isUndo: false)));
            Assert.Equal(expected, batch.points);
            // Live pitch is not undoable: no group was left open, and the command kept no
            // snapshot to restore, so unexecuting it changes nothing.
            Assert.False(DocManager.Inst.HasOpenUndoGroup);
            var applied = Snapshot(part.curves.First(c => c.abbr == PITD));
            batch.Unexecute();
            Assert.Equal(applied, Snapshot(part.curves.First(c => c.abbr == PITD)));
            var twin = ReplayOldFlow(project, part, original, expected, rpitCommand: false, simplify: false);
            Assert.Equal(Snapshot(twin), Snapshot(part.curves.First(c => c.abbr == PITD)));
        }

        /// <summary>
        /// The phrase fixture: a part at tick 960, rendered pitch on, no expression graphs, and
        /// a PITD curve with as many points as the canned pass writes samples.
        /// </summary>
        (UProject project, UVoicePart part, RenderPhrase phrase) Fixture(bool registerRpit) {
            int partPosition = -1;
            var renderer = new PitchRenderer(phrase => phrase.position == partPosition ? Result() : null);
            var (project, track, part) = PhraseSourceHashTest.BuildFixture(renderer);
            partPosition = part.position;
            track.RendererSettings.renderer = "TEST";
            if (registerRpit) {
                project.RegisterExpression(new UExpressionDescriptor("rendered pitch", RPIT, 2400, 10800, 6000) {
                    type = UExpressionType.MaskedCurve,
                });
            }
            // The undo group validates the whole project, which reads every note's first pitch point.
            foreach (var note in part.notes) {
                if (note.pitch.data.Count == 0) {
                    note.pitch.AddPoint(new PitchPoint(0, 0, PitchPointShape.io));
                }
            }
            var curve = part.curves.First(c => c.abbr == PITD);
            curve.xs.Clear();
            curve.ys.Clear();
            for (int i = 0; i < 8; i++) {
                curve.xs.Add(i * 60);
                curve.ys.Add(i % 2 == 0 ? -10 - i * 10 : 10 + i * 10);
            }
            part.renderPhrases.AddRange(RenderPhrase.FromPart(project, track, part));
            DocManager.Inst.TakeProjectForTest(project);
            return (project, part, part.renderPhrases[0]);
        }

        /// <summary>
        /// Frames 5 ticks apart from the phrase start, fully voiced: one grid sample each. One
        /// tone is far above the curve's range, one far below its minimum.
        /// </summary>
        static RenderPitchResult Result() {
            var tones = new[] { 60f, 60.2f, 120f, 10f, 60.4f, 60.6f, 60.8f, 61f };
            return new RenderPitchResult {
                ticks = Enumerable.Range(0, tones.Length).Select(i => (float)(i * 5)).ToArray(),
                tones = tones,
                voiced = tones.Select(_ => true).ToArray(),
            };
        }

        /// <summary>
        /// The samples the pass writes, transcribed from its loop for a result that is fully
        /// voiced with non-negative ticks inside the phrase: one sample per frame, the previous
        /// sample giving the write its span, and a sample at or below the curve's minimum left out.
        /// </summary>
        static List<(int x, int y, int lastX, int lastY)> ExpectedWrites(
                UProject project, UVoicePart part, RenderPhrase phrase, RenderPitchResult result) {
            int minPitD = (int)project.expressions[PITD].min;
            var writes = new List<(int x, int y, int lastX, int lastY)>();
            int? lastX = null;
            int? lastY = null;
            for (int i = 0; i < result.tones.Length; i++) {
                int x = phrase.position - part.position + (int)result.ticks[i];
                int pitchIndex = Math.Clamp((x - (phrase.position - part.position - phrase.leading)) / 5,
                    0, phrase.pitches.Length - 1);
                int y = (int)(result.tones[i] * 100 - phrase.pitchesBeforeDeviation[pitchIndex]);
                lastX ??= x;
                lastY ??= y;
                if (y > minPitD) {
                    writes.Add((x, y, lastX.Value, lastY.Value));
                }
                lastX = x;
                lastY = y;
            }
            return writes;
        }

        /// <summary>
        /// The old flow's end state: the same writes through one <see cref="SetCurveCommand"/>
        /// per sample on a twin part, then the group merge - which simplifies the curve when
        /// every command is a curve command, as it did at EndUndoGroup before.
        /// </summary>
        static UCurve ReplayOldFlow(UProject project, UVoicePart part, UCurve original,
                IEnumerable<(int x, int y, int lastX, int lastY)> writes, bool rpitCommand, bool simplify = true) {
            var twin = new UVoicePart { trackNo = part.trackNo, position = part.position };
            twin.curves.Add(original.Clone());
            var group = new UCommandGroup("command.batch.note", true);
            foreach (var (x, y, lastX, lastY) in writes) {
                var cmd = new SetCurveCommand(project, twin, PITD, x, y, lastX, lastY);
                group.Commands.Add(cmd);
                cmd.Execute();
            }
            if (rpitCommand) {
                var cmd = new ReplaceMaskedCurveCommand(twin, RPIT, new UMaskedCurve(RPIT));
                group.Commands.Add(cmd);
                cmd.Execute();
            }
            if (simplify) {
                group.Merge();
            }
            return twin.curves.First(c => c.abbr == PITD);
        }

        /// <summary>The curve as it is saved: its points, in order.</summary>
        static string Snapshot(UCurve curve) =>
            $"{curve.abbr}:{string.Join(",", curve.xs)}:{string.Join(",", curve.ys)}";

        class Recorder : ICmdSubscriber {
            public readonly List<(UCommand cmd, bool isUndo)> Commands = new List<(UCommand, bool)>();

            public void OnNext(UCommand cmd, bool isUndo) => Commands.Add((cmd, isUndo));

            /// <summary>The group's commands that touched the curve a pass writes.</summary>
            public IEnumerable<UCommand> PitchCurveCommands(bool isUndo) => Commands
                .Where(c => c.isUndo == isUndo)
                .Select(c => c.cmd)
                .Where(c => c is SetCurveCommand || c is SetCurveBatchCommand || c is MergedSetCurveCommand);
        }

        /// <summary>Rendered pitch is a canned result for the phrase the test picks.</summary>
        class PitchRenderer : IRenderer {
            readonly Func<RenderPhrase, RenderPitchResult> load;

            public PitchRenderer(Func<RenderPhrase, RenderPitchResult> load) => this.load = load;

            public USingerType SingerType => USingerType.Classic;
            public bool SupportsRenderPitch => true;
            public bool SupportsExpression(UExpressionDescriptor descriptor) => true;
            public RenderResult Layout(RenderPhrase phrase) => new RenderResult() {
                leadingMs = phrase.leadingMs,
                positionMs = phrase.positionMs,
                estimatedLengthMs = phrase.durationMs + phrase.leadingMs,
            };
            public Task<RenderResult> Render(RenderPhrase phrase, Progress progress, int trackNo,
                    System.Threading.CancellationTokenSource cancellation, bool isPreRender = false,
                    RenderPhraseEvents? renderEvents = null) => throw new NotImplementedException();
            public RenderPitchResult LoadRenderedPitch(RenderPhrase phrase) => load(phrase);
            public UExpressionDescriptor[] GetSuggestedExpressions(USinger singer, URenderSettings renderSettings) =>
                Array.Empty<UExpressionDescriptor>();
            // The renderer is resolved by its id, which the track keeps as this string.
            public override string ToString() => "TEST";
        }
    }
}

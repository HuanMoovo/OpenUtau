using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;

using OpenUtau.App.ViewModels;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

using Xunit;

namespace OpenUtau.UiTest {
    /// <summary>
    /// The piano roll's snap lines used to be produced by TickBackground.Render and read back by
    /// TickToLineTick, so snapping only worked after the background had painted once and every frame
    /// cleared and refilled the shared collection. NotesViewModel computes them now, from the same
    /// inputs the renderer had. This freezes the render-time traversal and the old TickToLineTick as
    /// the reference and checks the new path tick by tick, over several zooms, scroll positions, tick
    /// origins and snap divisions. It lives in this project because the view models are ReactiveObjects
    /// and need the app's process-wide initialization, like the other tests here.
    /// </summary>
    public class SnapLinesGoldenTest {
        // ViewConstants.MinTicklineWidth, frozen with the traversal: this reference must keep measuring
        // against the constant the renderer used, not follow the live one.
        const double MinTicklineWidth = 12.0;

        static UProject NewProject() {
            var project = new UProject(); // 4/4 from bar 0, 120 bpm
            project.timeSignatures.Add(new UTimeSignature(5, 3, 8));
            project.timeSignatures.Add(new UTimeSignature(11, 7, 8));
            project.tempos.Add(new UTempo(4800, 90));
            project.timeAxis.BuildSegments(project);
            return project;
        }

        /// <summary>
        /// The traversal TickBackground.Render ran before every paint, frozen here verbatim minus the
        /// drawing: same snap unit, same bar walk, same "lines between bars" rule, same trailing bar.
        /// </summary>
        static List<int> RenderDrivenSnapTicks(UProject project, double tickWidth, double tickOffset, int tickOrigin, double viewportWidth, int snapDiv) {
            var snapTicks = new List<int>();
            int snapUnit = project.resolution * 4 / snapDiv;
            while (snapUnit * tickWidth < MinTicklineWidth) {
                snapUnit *= 2; // Avoid drawing too dense.
            }
            double minLineTick = MinTicklineWidth / tickWidth;
            double leftTick = tickOffset + tickOrigin;
            double rightTick = tickOffset + tickOrigin + viewportWidth / tickWidth;

            project.timeAxis.TickPosToBarBeat(tickOrigin, out int bar, out int beat, out int remainingTicks);
            if (bar > 0) {
                bar--;
            }
            int barTick = project.timeAxis.BarBeatToTickPos(bar, 0);
            while (barTick <= rightTick) {
                snapTicks.Add(barTick);
                var timeSig = project.timeAxis.TimeSignatureAtBar(bar);
                int nextBarTick = project.timeAxis.BarBeatToTickPos(bar + 1, 0);
                int ticksPerBeat = project.resolution * 4 * timeSig.beatPerBar / timeSig.beatUnit;
                int ticksPerLine = snapUnit;
                if (ticksPerBeat < snapUnit) {
                    ticksPerLine = ticksPerBeat;
                } else if (ticksPerBeat % snapUnit != 0) {
                    if (ticksPerBeat > minLineTick) {
                        ticksPerLine = ticksPerBeat;
                    } else {
                        ticksPerLine = nextBarTick - barTick;
                    }
                }
                if (nextBarTick > leftTick) {
                    for (int tick = barTick + ticksPerLine; tick < nextBarTick; tick += ticksPerLine) {
                        snapTicks.Add(tick);
                    }
                }
                barTick = nextBarTick;
                bar++;
            }
            snapTicks.Add(barTick);
            return snapTicks;
        }

        /// <summary>The old TickToLineTick, over the list the renderer had produced.</summary>
        static void RenderDrivenTickToLineTick(List<int> snapTicks, UProject project, int tick, int tickOrigin, out int left, out int right) {
            if (snapTicks.Count == 0) {
                left = 0;
                right = project.resolution;
                return;
            }
            int index = snapTicks.BinarySearch(tick + tickOrigin);
            if (index < 0) {
                index = ~index - 1;
            }
            if (0 >= snapTicks.Count - 2) {
                left = right = tick;
                return;
            }
            index = Math.Clamp(index, 0, snapTicks.Count - 2);
            left = snapTicks[index] - tickOrigin;
            right = snapTicks[index + 1] - tickOrigin;
        }

        /// <summary>Installs a project for the body and puts the previous one back afterwards: the view
        /// models read the document from DocManager, and are created inside the headless session, which
        /// initializes the app's process-wide state (ReactiveUI among it).</summary>
        static void WithProject(Action<UProject, NotesViewModel> body) {
            var previous = DocManager.Inst.Project;
            var project = NewProject();
            try {
                HeadlessUi.Run(() => {
                    var notesViewModel = new NotesViewModel();
                    DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
                    body(project, notesViewModel);
                });
            } finally {
                HeadlessUi.Run(() => DocManager.Inst.ExecuteCmd(new LoadProjectNotification(previous)));
            }
        }

        /// <summary>Puts the view into the state a render would have seen.</summary>
        static void Arrange(NotesViewModel notesViewModel, double tickWidth, double tickOffset, int tickOrigin, double viewportWidth, int snapDiv) {
            // A long part keeps TickCount above the viewport, so the zoom and scroll the test asks for
            // are not clamped away by the whole-part-fits rule.
            notesViewModel.Part = new UVoicePart { Duration = 480 * 4 * 256 };
            notesViewModel.Bounds = new Rect(0, 0, viewportWidth, 400);
            notesViewModel.TickWidth = tickWidth; // also re-derives the automatic snap division
            notesViewModel.TickOffset = tickOffset;
            notesViewModel.TickOrigin = tickOrigin;
            notesViewModel.SnapDiv = snapDiv; // set last: the zoom subscription would overwrite it
            Assert.Equal(tickWidth, notesViewModel.TickWidth);
            Assert.Equal(tickOffset, notesViewModel.TickOffset);
            Assert.Equal(tickOrigin, notesViewModel.TickOrigin);
            Assert.Equal(snapDiv, notesViewModel.SnapDiv);
        }

        [Theory]
        [InlineData(4.0 / 480.0, 0.0, 0, 1200.0, 4)]     // zoomed all the way out
        [InlineData(0.021, 0.0, 0, 1600.0, 4)]           // snap unit doubles to stay above min line width
        [InlineData(0.267, 0.0, 0, 0.0, 4)]              // laid out but no width yet
        [InlineData(0.26, 480.0, 0, 1000.0, 4)]
        [InlineData(0.26, 1920.5, 0, 800.0, 3)]          // non power of two snap division
        [InlineData(0.5, 12000.0, 480, 1500.0, 8)]       // scrolled into the 3/8 section, offset part
        [InlineData(1.0, 9600.0, 9600, 600.0, 16)]       // part starting inside the 3/8 section
        [InlineData(0.13, 3456.7, 960, 1313.0, 6)]
        [InlineData(1.3333, 8000.0, 14400, 400.0, 2)]    // into the 7/8 section
        public void TickToLineTickMatchesTheRenderDrivenPath(double tickWidth, double tickOffset, int tickOrigin, double viewportWidth, int snapDiv) {
            WithProject((project, notesViewModel) => {
                Arrange(notesViewModel, tickWidth, tickOffset, tickOrigin, viewportWidth, snapDiv);
                var referenceTicks = RenderDrivenSnapTicks(project,
                    notesViewModel.TickWidth, notesViewModel.TickOffset, notesViewModel.TickOrigin,
                    notesViewModel.Bounds.Width, notesViewModel.SnapDiv);

                notesViewModel.UpdateSnapLines();

                Assert.Equal(referenceTicks, notesViewModel.SnapLines);
                foreach (int tick in TicksToCheck(referenceTicks, notesViewModel.TickWidth, notesViewModel.TickOffset, notesViewModel.TickOrigin, notesViewModel.Bounds.Width)) {
                    notesViewModel.TickToLineTick(tick, out int left, out int right);
                    RenderDrivenTickToLineTick(referenceTicks, project, tick, notesViewModel.TickOrigin, out int expectedLeft, out int expectedRight);
                    Assert.True(expectedLeft == left && expectedRight == right,
                        $"tick {tick}: expected ({expectedLeft}, {expectedRight}), got ({left}, {right})");
                }
            });
        }

        static IEnumerable<int> TicksToCheck(List<int> referenceTicks, double tickWidth, double tickOffset, int tickOrigin, double viewportWidth) {
            int visibleStart = (int)(tickOffset + tickOrigin);
            int visibleEnd = (int)(tickOffset + tickOrigin + viewportWidth / tickWidth);
            int step = Math.Max(1, (visibleEnd - visibleStart) / 64);
            for (int tick = visibleStart - 2 * 1920; tick <= visibleEnd + 2 * 1920; tick += step) {
                yield return tick;
            }
            // Every line, and both of its neighbours, which is where the bracketing changes.
            foreach (int line in referenceTicks) {
                yield return line - tickOrigin - 1;
                yield return line - tickOrigin;
                yield return line - tickOrigin + 1;
            }
            yield return int.MinValue + 1;
            yield return -1920;
            yield return 0;
            yield return 4 * 480 * 256 + 1000;
        }

        [Fact]
        public void SnapLinesExistAndBracketWithoutAnyPaint() {
            WithProject((project, notesViewModel) => {
                // The old path: TickToLineTick read the list TickBackground.Render had filled, so before
                // the first paint it returned the (0, resolution) fallback for every tick. Snapping must
                // be right as soon as the view knows where it is.
                Arrange(notesViewModel, 200.0 / 480.0, 0, 0, 1000.0, 4);

                Assert.Equal(new[] { 0, 480, 960, 1440, 1920, 2400, 2880, 3360, 3840 }, notesViewModel.SnapLines);
                notesViewModel.TickToLineTick(700, out int left, out int right);
                Assert.Equal((480, 960), (left, right));
                notesViewModel.TickToLineTick(1919, out left, out right);
                Assert.Equal((1440, 1920), (left, right));
                notesViewModel.TickToLineTick(1920, out left, out right);
                Assert.Equal((1920, 2400), (left, right));
            });
        }

        [Fact]
        public void ZoomingAndScrollingKeepSnappingEqualToTheRenderDrivenPath() {
            WithProject((project, notesViewModel) => {
                // The same walk the user makes: zoom in and out, scroll, and change the snap division,
                // checking after each step that snapping still matches what the renderer would have
                // produced for that state.
                Arrange(notesViewModel, 0.26, 0, 0, 1000.0, 4);
                var states = new (double TickWidth, double TickOffset, int SnapDiv)[] {
                    (0.26, 0.0, 4), (0.26, 0.0, 8), (0.5, 0.0, 8), (1.0, 500.0, 16),
                    (0.5, 500.0, 16), (0.1, 500.0, 2), (0.0104, 20000.0, 4), (0.0104, 20000.0, 24),
                };
                foreach (var state in states) {
                    notesViewModel.TickWidth = state.TickWidth;
                    notesViewModel.TickOffset = state.TickOffset;
                    notesViewModel.SnapDiv = state.SnapDiv;
                    var referenceTicks = RenderDrivenSnapTicks(project,
                        notesViewModel.TickWidth, notesViewModel.TickOffset, notesViewModel.TickOrigin,
                        notesViewModel.Bounds.Width, notesViewModel.SnapDiv);
                    Assert.Equal(referenceTicks, notesViewModel.SnapLines);
                    foreach (int tick in TicksToCheck(referenceTicks, notesViewModel.TickWidth, notesViewModel.TickOffset, notesViewModel.TickOrigin, notesViewModel.Bounds.Width)) {
                        notesViewModel.TickToLineTick(tick, out int left, out int right);
                        RenderDrivenTickToLineTick(referenceTicks, project, tick, notesViewModel.TickOrigin, out int expectedLeft, out int expectedRight);
                        Assert.True(expectedLeft == left && expectedRight == right,
                            $"zoom {state.TickWidth} offset {state.TickOffset} div {state.SnapDiv}, tick {tick}: " +
                            $"expected ({expectedLeft}, {expectedRight}), got ({left}, {right})");
                    }
                }
            });
        }

        [Fact]
        public void SnapLinesFollowTheTimeSignatureChange() {
            WithProject((project, notesViewModel) => {
                // A time signature edit rebuilds the axis in place, which the snap lines must pick up
                // even though none of the view's own inputs changed. The app invalidates them from
                // PlaybackViewModel's TimeAxisChangedEvent; the test calls the same entry point directly.
                Arrange(notesViewModel, 0.5, 9600, 0, 1200.0, 4);
                var before = notesViewModel.SnapLines.ToArray();
                Assert.Contains(9600, before);  // bar 5
                Assert.Contains(10320, before); // start of the 3/8 section's second bar, 720 ticks wide

                project.timeSignatures[1] = new UTimeSignature(5, 4, 4);
                project.timeAxis.BuildSegments(project);
                notesViewModel.InvalidateSnapLines();

                var expectedTicks = RenderDrivenSnapTicks(project,
                    notesViewModel.TickWidth, notesViewModel.TickOffset, notesViewModel.TickOrigin,
                    notesViewModel.Bounds.Width, notesViewModel.SnapDiv);
                Assert.Equal(expectedTicks, notesViewModel.SnapLines);
                Assert.NotEqual(before, notesViewModel.SnapLines);
            });
        }
        [Fact]
        public void SnapLinesFollowTheProject() {
            WithProject((project, notesViewModel) => {
                // Loading another project replaces DocManager's; the view must not keep snapping against
                // the old one's lines. This one is in 3/4 from the start, so its lines differ in the
                // viewport.
                Arrange(notesViewModel, 0.5, 0, 0, 1200.0, 4);
                var before = notesViewModel.SnapLines.ToArray();
                Assert.NotEmpty(before);

                var other = new UProject();
                other.timeSignatures.Clear();
                other.timeSignatures.Add(new UTimeSignature(0, 3, 4));
                other.timeAxis.BuildSegments(other);
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(other));
                notesViewModel.UpdateSnapLines();

                Assert.Equal(RenderDrivenSnapTicks(other,
                    notesViewModel.TickWidth, notesViewModel.TickOffset, notesViewModel.TickOrigin,
                    notesViewModel.Bounds.Width, notesViewModel.SnapDiv), notesViewModel.SnapLines);
                Assert.NotEqual(before, notesViewModel.SnapLines);
            });
        }
    }
}

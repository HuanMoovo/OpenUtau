using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

using OpenUtau.App.Controls;
using OpenUtau.App.ViewModels;
using OpenUtau.App.Views;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

using Xunit;

namespace OpenUtau.UiTest {
    /// <summary>
    /// The playhead of issue #2333, on the UI side. A press on a timeline seeks, and both timelines -
    /// the workspace's and the piano roll's - put their playhead marker where the tick is, also after
    /// the view has been zoomed or scrolled under it. What moves the playhead during playback is the
    /// audio clock, which the headless harness has none of; that is the side the issue's own comments
    /// trace back to rendering, not to these bindings.
    /// </summary>
    public class PlayheadTest {
        const double TickWidth = 1.0 / 6.0;

        static Button FindButtonWithText(Window window, string resourceKey) {
            var text = window.FindResource(resourceKey) as string;
            Assert.False(string.IsNullOrEmpty(text));
            var button = window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.IsEffectivelyVisible &&
                    b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text));
            Assert.NotNull(button);
            return button;
        }

        static void Click(Window window, Point point) {
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            HeadlessUi.Flush();
        }

        [Fact]
        public void BothTimelinesPutTheirPlayheadWhereThePlayPositionIs() {
            HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                HeadlessUi.Errors.Clear();
                var window = new MainWindow { Width = 1280, Height = 800 };
                try {
                    window.Show();
                    window.InitProject();
                    HeadlessUi.Flush();
                    var welcomeNew = FindButtonWithText(window, "welcome.new");
                    Click(window, welcomeNew.TranslatePoint(
                        new Point(welcomeNew.Bounds.Width / 2, welcomeNew.Bounds.Height / 2), window)!.Value);
                    var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
                    var project = DocManager.Inst.Project;
                    var tracksViewModel = viewModel.TracksViewModel;

                    // A part, so the piano roll has something to open, and a fixed zoom so the tick a
                    // point stands for is known.
                    int partEnd = project.timeAxis.BarBeatToTickPos(3, 0);
                    DocManager.Inst.StartUndoGroup("command.part.add");
                    var part = new UVoicePart { trackNo = 0, position = 0, Duration = partEnd };
                    DocManager.Inst.ExecuteCmd(new AddPartCommand(project, part));
                    DocManager.Inst.EndUndoGroup();
                    tracksViewModel.TickWidth = TickWidth;
                    tracksViewModel.TickOffset = 0;
                    HeadlessUi.Flush();

                    // The workspace timeline: a press seeks there, and the marker is drawn at that tick.
                    var timeline = window.FindControl<Canvas>("TimelineCanvas")!;
                    Assert.True(timeline.Bounds.Width > 0 && timeline.Bounds.Height > 0, "the timeline has no size");
                    var marker = window.FindControl<Path>("PlayPos");
                    Assert.NotNull(marker);
                    int seekTick = project.timeAxis.BarBeatToTickPos(1, 0);
                    Point OnTimeline(int tick) => timeline.TranslatePoint(new Point(
                        (tick - tracksViewModel.TickOffset) * tracksViewModel.TickWidth,
                        timeline.Bounds.Height / 2), window)!.Value;
                    Click(window, OnTimeline(seekTick));
                    Assert.Equal(seekTick, DocManager.Inst.playPosTick);
                    Assert.Equal(seekTick * TickWidth, tracksViewModel.PlayPosX, 1);
                    Assert.Equal(seekTick * TickWidth,
                        marker!.TranslatePoint(new Point(0, 0), window)!.Value.X - timeline.Bounds.X, 1);

                    // Same in the piano roll: the marker, its line, and the cell it highlights all
                    // follow the play position.
                    var pianoRollViewModel = new PianoRollViewModel();
                    pianoRollViewModel.PlaybackViewModel = viewModel.PlaybackViewModel;
                    DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, project, 0));
                    var notesViewModel = pianoRollViewModel.NotesViewModel;
                    Assert.Same(part, notesViewModel.Part);
                    notesViewModel.TickWidth = TickWidth;
                    notesViewModel.TickOffset = 0;
                    var pianoRoll = new PianoRoll(pianoRollViewModel) { MainWindow = window };
                    window.FindControl<ContentControl>("PianoRollContainer")!.Content = pianoRoll;
                    viewModel.ShowPianoRoll = true;
                    HeadlessUi.Flush();
                    var rollTimeline = pianoRoll.FindControl<Canvas>("TimelineCanvas")!;
                    var rollMarker = pianoRoll.FindControl<Path>("PlayPos");
                    Assert.NotNull(rollMarker);
                    Assert.True(rollTimeline.Bounds.Width > 0 && rollTimeline.Bounds.Height > 0,
                        "the piano roll timeline has no size");
                    Assert.Equal(seekTick, notesViewModel.PlayPosTick);
                    Assert.Equal(seekTick * TickWidth, notesViewModel.PlayPosX, 1);

                    // Zooming and scrolling move the marker with the ticks under it, instead of leaving
                    // it at the position of the previous view.
                    notesViewModel.TickWidth = TickWidth * 3;
                    HeadlessUi.Flush();
                    Assert.Equal(seekTick * TickWidth * 3, notesViewModel.PlayPosX, 1);
                    notesViewModel.TickOffset = seekTick;
                    HeadlessUi.Flush();
                    Assert.Equal(0, notesViewModel.PlayPosX, 1);
                    Assert.Equal(0, rollMarker!.TranslatePoint(new Point(0, 0), window)!.Value.X -
                        rollTimeline.TranslatePoint(new Point(0, 0), window)!.Value.X, 1);

                    // Seeking behind the part moves the piano roll's marker left of the view, like the
                    // workspace's: nothing here is part relative except the piano roll's own numbers.
                    DocManager.Inst.ExecuteCmd(new SeekPlayPosTickNotification(0));
                    HeadlessUi.Flush();
                    Assert.Equal(0, DocManager.Inst.playPosTick);
                    Assert.Equal(0, notesViewModel.PlayPosTick);
                    Assert.Equal(0, tracksViewModel.PlayPosX, 1);

                    var errors = HeadlessUi.Errors.Snapshot();
                    Assert.All(errors, error => Assert.Contains(nameof(SearchNoteViewModel), error));
                } finally {
                    DocManager.Inst.Undo();
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                    HeadlessUi.SaveScreenshot(window, nameof(BothTimelinesPutTheirPlayheadWhereThePlayPositionIs));
                    window.Close();
                }
            });
        }
    }
}

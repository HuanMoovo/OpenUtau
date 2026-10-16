using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
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
    /// Dragging a tempo marker on the timeline (issue #2219). The press seeks, the way every
    /// timeline press always did, including a click that does not move; the marker follows the
    /// pointer only once it has travelled past the drag threshold, and the drop is one undo group.
    /// The marker at tick 0 is the base tempo and stays put.
    /// </summary>
    public class TempoDragTest {
        // Zoomed out far enough that the first bars sit well inside the timeline canvas.
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
        public void DraggingATempoMarkerMovesItInOneUndoStep() {
            HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                HeadlessUi.Errors.Clear();
                var window = new MainWindow { Width = 1280, Height = 800 };
                try {
                    window.Show();
                    window.InitProject();
                    HeadlessUi.Flush();
                    // A project, through the welcome page, as the other tests make one.
                    var welcomeNew = FindButtonWithText(window, "welcome.new");
                    Click(window, welcomeNew.TranslatePoint(
                        new Point(welcomeNew.Bounds.Width / 2, welcomeNew.Bounds.Height / 2), window)!.Value);
                    HeadlessUi.Flush();
                    var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
                    Assert.Equal(1, viewModel.Page);
                    var project = DocManager.Inst.Project;
                    var tracksVm = viewModel.TracksViewModel;
                    tracksVm.TickWidth = TickWidth;
                    tracksVm.TickOffset = 0;
                    HeadlessUi.Flush();

                    // Two tempos: the project's own at tick 0, and one at the start of bar 2 that the
                    // drag moves to the start of bar 3. Ticks come from the time axis, so this does not
                    // depend on the resolution or time signature of whatever project was loaded.
                    int fromTick = project.timeAxis.BarBeatToTickPos(1, 0);
                    int toTick = project.timeAxis.BarBeatToTickPos(2, 0);
                    int midTick = (fromTick + toTick) / 2;
                    Assert.True(fromTick > 0 && midTick > fromTick && toTick > midTick);
                    DocManager.Inst.StartUndoGroup("command.project.tempo");
                    DocManager.Inst.ExecuteCmd(new AddTempoChangeCommand(project, fromTick, 150));
                    DocManager.Inst.EndUndoGroup();
                    HeadlessUi.Flush();
                    Assert.Equal(new[] { 0, fromTick }, project.tempos.Select(tempo => tempo.position));
                    Assert.Equal(150, project.timeAxis.GetBpmAtTick(fromTick));

                    var timeline = window.FindControl<Canvas>("TimelineCanvas");
                    Assert.NotNull(timeline);
                    Assert.True(timeline.Bounds.Width > 0 && timeline.Bounds.Height > 0, "the timeline canvas has no size");
                    var preview = window.FindControl<Border>("TempoDragPreview");
                    Assert.NotNull(preview);
                    Assert.False(preview.IsVisible);
                    Point OnTimeline(int tick) => timeline.TranslatePoint(
                        new Point((tick - tracksVm.TickOffset) * tracksVm.TickWidth, timeline.Bounds.Height / 2), window)!.Value;
                    double PreviewX() => preview.TranslatePoint(new Point(0, 0), window)!.Value.X;

                    // Drag the bar 2 marker to bar 3. The press seeks, the moves do not: they move the
                    // marker, and the preview line shows where it would drop, snapped to a line tick.
                    window.MouseDown(OnTimeline(fromTick), MouseButton.Left, RawInputModifiers.None);
                    HeadlessUi.Flush();
                    Assert.Equal(fromTick, DocManager.Inst.playPosTick);
                    Assert.False(preview.IsVisible);
                    window.MouseMove(OnTimeline(midTick), RawInputModifiers.LeftMouseButton);
                    HeadlessUi.Flush();
                    Assert.Equal(fromTick, DocManager.Inst.playPosTick);
                    Assert.True(preview.IsVisible);
                    Assert.Equal(OnTimeline(midTick).X, PreviewX(), 1);
                    window.MouseMove(OnTimeline(toTick), RawInputModifiers.LeftMouseButton);
                    HeadlessUi.Flush();
                    Assert.Equal(OnTimeline(toTick).X, PreviewX(), 1);
                    window.MouseUp(OnTimeline(toTick), MouseButton.Left, RawInputModifiers.None);
                    HeadlessUi.Flush();

                    // The drop moved it, keeping its bpm, and the time axis follows the move: bar 2 is
                    // on the project's own tempo again.
                    Assert.False(preview.IsVisible);
                    Assert.Equal(new[] { 0, toTick }, project.tempos.Select(tempo => tempo.position));
                    Assert.Equal(150, project.tempos.Single(tempo => tempo.position == toTick).bpm);
                    Assert.Equal(120, project.timeAxis.GetBpmAtTick(fromTick));
                    Assert.Equal(150, project.timeAxis.GetBpmAtTick(toTick));

                    // One undo puts the marker back, its bpm with it, and leaves the other tempo alone.
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                    Assert.Equal(new[] { 0, fromTick }, project.tempos.Select(tempo => tempo.position));
                    Assert.Equal(150, project.tempos.Single(tempo => tempo.position == fromTick).bpm);
                    Assert.Equal(150, project.timeAxis.GetBpmAtTick(fromTick));

                    // The base tempo at tick 0 is not draggable: a drag starting on it keeps seeking.
                    window.MouseDown(OnTimeline(0), MouseButton.Left, RawInputModifiers.None);
                    window.MouseMove(OnTimeline(midTick), RawInputModifiers.LeftMouseButton);
                    window.MouseUp(OnTimeline(midTick), MouseButton.Left, RawInputModifiers.None);
                    HeadlessUi.Flush();
                    Assert.Equal(new[] { 0, fromTick }, project.tempos.Select(tempo => tempo.position));
                    Assert.Equal(120, project.tempos[0].bpm);
                    Assert.Equal(midTick, DocManager.Inst.playPosTick);

                    // Escape drops the drag in progress: nothing moves, and the preview goes away.
                    window.MouseDown(OnTimeline(fromTick), MouseButton.Left, RawInputModifiers.None);
                    window.MouseMove(OnTimeline(toTick), RawInputModifiers.LeftMouseButton);
                    HeadlessUi.Flush();
                    Assert.True(preview.IsVisible);
                    window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                    HeadlessUi.Flush();
                    Assert.False(preview.IsVisible);
                    window.MouseUp(OnTimeline(toTick), MouseButton.Left, RawInputModifiers.None);
                    HeadlessUi.Flush();
                    Assert.Equal(new[] { 0, fromTick }, project.tempos.Select(tempo => tempo.position));

                    // So does dragging the pointer out of the timeline.
                    var outside = OnTimeline(toTick) + new Vector(0, timeline.Bounds.Height + 20);
                    window.MouseDown(OnTimeline(fromTick), MouseButton.Left, RawInputModifiers.None);
                    window.MouseMove(OnTimeline(toTick), RawInputModifiers.LeftMouseButton);
                    HeadlessUi.Flush();
                    Assert.True(preview.IsVisible);
                    window.MouseMove(outside, RawInputModifiers.LeftMouseButton);
                    HeadlessUi.Flush();
                    Assert.False(preview.IsVisible);
                    window.MouseUp(outside, MouseButton.Left, RawInputModifiers.None);
                    HeadlessUi.Flush();
                    Assert.Equal(new[] { 0, fromTick }, project.tempos.Select(tempo => tempo.position));

                    // A plain click that does not move still seeks, and moves no marker.
                    Click(window, OnTimeline(fromTick));
                    Assert.Equal(fromTick, DocManager.Inst.playPosTick);
                    Assert.Equal(new[] { 0, fromTick }, project.tempos.Select(tempo => tempo.position));

                    var errors = HeadlessUi.Errors.Snapshot();
                    Assert.True(errors.Count == 0, "Errors reported:\n" + string.Join("\n", errors));
                } finally {
                    // Leave the shared document as the test found it: the other headless tests click
                    // through the welcome page's new project flow, which stops to ask about unsaved
                    // changes. Loading the project cleared the undo queue, so this test's own two
                    // groups (the second tempo, and the drag the test already undid) are all that is
                    // left; the second undo is a no-op when the drag's undo already ran.
                    DocManager.Inst.Undo();
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                    HeadlessUi.SaveScreenshot(window, nameof(DraggingATempoMarkerMovesItInOneUndoStep));
                    window.Close();
                }
            });
        }

        [Fact]
        public void DraggingATempoMarkerOnThePianoRollTimelineMovesIt() {
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
                    HeadlessUi.Flush();
                    var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
                    var project = DocManager.Inst.Project;

                    int fromTick = project.timeAxis.BarBeatToTickPos(1, 0);
                    int toTick = project.timeAxis.BarBeatToTickPos(2, 0);
                    DocManager.Inst.StartUndoGroup("command.part.add");
                    var part = new UVoicePart { trackNo = 0, position = 0, Duration = toTick * 2 };
                    DocManager.Inst.ExecuteCmd(new AddPartCommand(project, part));
                    DocManager.Inst.EndUndoGroup();
                    DocManager.Inst.StartUndoGroup("command.project.tempo");
                    DocManager.Inst.ExecuteCmd(new AddTempoChangeCommand(project, fromTick, 150));
                    DocManager.Inst.EndUndoGroup();
                    HeadlessUi.Flush();

                    // The piano roll on that part, built the way PianoRollSnapTest builds it: the
                    // loading popup its double click raises does not finish under the headless timer.
                    var pianoRollViewModel = new PianoRollViewModel();
                    pianoRollViewModel.PlaybackViewModel = viewModel.PlaybackViewModel;
                    DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, project, 0));
                    var notesVm = pianoRollViewModel.NotesViewModel;
                    Assert.Same(part, notesVm.Part);
                    notesVm.TickWidth = TickWidth;
                    notesVm.TickOffset = 0;
                    var pianoRoll = new PianoRoll(pianoRollViewModel) { MainWindow = window };
                    window.FindControl<ContentControl>("PianoRollContainer")!.Content = pianoRoll;
                    viewModel.ShowPianoRoll = true;
                    HeadlessUi.Flush();

                    // This timeline is part relative, so its x comes from the part open in the roll.
                    var timeline = pianoRoll.FindControl<Canvas>("TimelineCanvas");
                    Assert.NotNull(timeline);
                    Assert.True(timeline.Bounds.Width > 0 && timeline.Bounds.Height > 0, "the piano roll timeline has no size");
                    var preview = pianoRoll.FindControl<Border>("TempoDragPreview");
                    Assert.NotNull(preview);
                    Point OnTimeline(int tick) => timeline.TranslatePoint(new Point(
                        (tick - notesVm.TickOrigin - notesVm.TickOffset) * notesVm.TickWidth,
                        timeline.Bounds.Height / 2), window)!.Value;

                    window.MouseDown(OnTimeline(fromTick), MouseButton.Left, RawInputModifiers.None);
                    window.MouseMove(OnTimeline(toTick), RawInputModifiers.LeftMouseButton);
                    HeadlessUi.Flush();
                    Assert.True(preview.IsVisible);
                    Assert.Equal(OnTimeline(toTick).X, preview.TranslatePoint(new Point(0, 0), window)!.Value.X, 1);
                    window.MouseUp(OnTimeline(toTick), MouseButton.Left, RawInputModifiers.None);
                    HeadlessUi.Flush();

                    Assert.False(preview.IsVisible);
                    Assert.Equal(new[] { 0, toTick }, project.tempos.Select(tempo => tempo.position));
                    Assert.Equal(150, project.tempos.Single(tempo => tempo.position == toTick).bpm);

                    // One undo, as on the main window's timeline.
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                    Assert.Equal(new[] { 0, fromTick }, project.tempos.Select(tempo => tempo.position));

                    var errors = HeadlessUi.Errors.Snapshot();
                    Assert.All(errors, error => Assert.Contains(nameof(SearchNoteViewModel), error));
                } finally {
                    // The part, the second tempo and any drag left in the queue go away again.
                    DocManager.Inst.Undo();
                    DocManager.Inst.Undo();
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                    HeadlessUi.SaveScreenshot(window, nameof(DraggingATempoMarkerOnThePianoRollTimelineMovesIt));
                    window.Close();
                }
            });
        }
    }
}

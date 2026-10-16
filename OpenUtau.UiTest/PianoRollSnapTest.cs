using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

using OpenUtau.App.Controls;
using OpenUtau.App.ViewModels;
using OpenUtau.App.Views;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

using Xunit;

namespace OpenUtau.UiTest {
    /// <summary>
    /// The piano roll's snap lines used to be written by the background control while painting, so
    /// snapping only started working after the piano roll's first frame, and everything that ran
    /// before it snapped on a (0, resolution) fallback. The view model computes them now.
    /// </summary>
    public class PianoRollSnapTest {
        static void Click(Window window, Point point) {
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }

        static Button FindButtonWithText(Window window, string resourceKey) {
            var text = window.FindResource(resourceKey) as string;
            Assert.False(string.IsNullOrEmpty(text));
            var button = window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.IsEffectivelyVisible &&
                    b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text));
            Assert.NotNull(button);
            return button;
        }

        [Fact]
        public void SnappingIsCorrectBeforeThePianoRollsFirstFrame() {
            HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                HeadlessUi.Errors.Clear();
                var window = new MainWindow { Width = 1280, Height = 800 };
                try {
                    window.Show();
                    window.InitProject();
                    HeadlessUi.Flush();
                    // A project, through the welcome page.
                    var welcomeNew = FindButtonWithText(window, "welcome.new");
                    Click(window, welcomeNew.TranslatePoint(
                        new Point(welcomeNew.Bounds.Width / 2, welcomeNew.Bounds.Height / 2), window)!.Value);
                    HeadlessUi.Flush();
                    var mainViewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
                    Assert.Equal(1, mainViewModel.Page);
                    var project = DocManager.Inst.Project;

                    // A part at the start of the first track, added the way the view models add one.
                    var part = new UVoicePart { trackNo = 0, position = 0, Duration = 480 * 4 * 4 };
                    DocManager.Inst.StartUndoGroup("command.part.add");
                    DocManager.Inst.ExecuteCmd(new AddPartCommand(project, part));
                    DocManager.Inst.EndUndoGroup();
                    HeadlessUi.Flush();
                    var partControl = window.GetVisualDescendants().OfType<PartControl>()
                        .Single(control => control.part == part);
                    Assert.True(partControl.Bounds.Width > 0 && partControl.Bounds.Height > 0);

                    // Open the piano roll on it. The view model and the part load notification are what
                    // PartsCanvasDoubleTapped passes to after its double click; that handler also raises a
                    // modal loading popup and builds the control in a worker continuation, which does not
                    // complete under the headless render timer, so the control is attached below the way
                    // the handler attaches it. The snap state under test is the view model's.
                    var pianoRollViewModel = new PianoRollViewModel();
                    pianoRollViewModel.PlaybackViewModel = mainViewModel.PlaybackViewModel;
                    DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, project, 0));
                    var notesViewModel = pianoRollViewModel.NotesViewModel;
                    Assert.Same(part, notesViewModel.Part);
                    Assert.Equal(0, notesViewModel.TickOrigin);

                    // Where the piano roll sits: fixed zoom, scrolled to the start of the part, quarter
                    // snapping. No frame has been rendered since the part was opened, so this is exactly
                    // the state the old code had an empty snap list in.
                    notesViewModel.TickWidth = 200.0 / 480.0;
                    notesViewModel.TickOffset = 0;
                    notesViewModel.SnapDiv = 4;

                    // Snapping already works: the lines exist, and a tick inside the part is bracketed by
                    // its neighbours. The old fallback returned (0, resolution) for every tick here.
                    Assert.NotEmpty(notesViewModel.SnapLines);
                    notesViewModel.TickToLineTick(700, out int left, out int right);
                    Assert.Equal((480, 960), (left, right));

                    // The background paints the lines the view model computed, and follows them when the
                    // view moves.
                    var pianoRoll = new PianoRoll(pianoRollViewModel) { MainWindow = window };
                    window.FindControl<ContentControl>("PianoRollContainer")!.Content = pianoRoll;
                    mainViewModel.ShowPianoRoll = true;
                    HeadlessUi.Flush();
                    var tickBackground = pianoRoll.GetVisualDescendants().OfType<TickBackground>().First();
                    Assert.Same(notesViewModel.SnapLines, tickBackground.SnapLines);

                    notesViewModel.TickOffset = 480;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Same(notesViewModel.SnapLines, tickBackground.SnapLines);
                    notesViewModel.TickToLineTick(700, out left, out right);
                    Assert.Equal((480, 960), (left, right));

                    // Nothing else went wrong opening the roll. The one known noise source is its search
                    // bar, whose bindings wait for the SearchNoteViewModel that SearchBar.Show installs
                    // when the search panel is opened (the app logs those same binding errors).
                    Assert.All(HeadlessUi.Errors.Snapshot(),
                        error => Assert.Contains(nameof(SearchNoteViewModel), error));
                } finally {
                    // Leave the shared document as the test found it: the other headless tests click
                    // through the welcome page's new project flow, which stops to ask about unsaved
                    // changes.
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                    HeadlessUi.SaveScreenshot(window, nameof(SnappingIsCorrectBeforeThePianoRollsFirstFrame));
                    window.Close();
                }
            });
        }
    }
}

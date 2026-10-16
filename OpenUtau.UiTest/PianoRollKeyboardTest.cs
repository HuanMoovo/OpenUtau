using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

using OpenUtau.App;
using OpenUtau.App.Controls;
using OpenUtau.App.ViewModels;
using OpenUtau.App.Views;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

using ReactiveUI;

using Xunit;

namespace OpenUtau.UiTest {
    /// <summary>
    /// The piano roll keyboard's degree names (issue #2005): they are the scale hints the piano roll
    /// shows next to the note names, and they were left showing the wrong scale in two ways. The key
    /// they are relative to was only read when a part was opened, so changing the project key - from
    /// the main window, or by undoing a change made here - left the old degrees on the keys; and the
    /// keyboard was never repainted when the degree style changed, so switching it on did nothing
    /// until something else happened to redraw the keyboard.
    /// </summary>
    public class PianoRollKeyboardTest {
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

        /// <summary>What the window shows where this control sits, for comparing two frames.</summary>
        static byte[] CaptureControl(Window window, Control control) {
            var origin = control.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(origin);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            using var buffer = frame!.Lock();
            int x0 = Math.Max(0, (int)origin!.Value.X);
            int y0 = Math.Max(0, (int)origin.Value.Y);
            int x1 = Math.Min(x0 + (int)control.Bounds.Width, buffer.Size.Width);
            int y1 = Math.Min(y0 + (int)control.Bounds.Height, buffer.Size.Height);
            Assert.True(x1 > x0 && y1 > y0, "the control has no rendered area");
            var pixels = new byte[(x1 - x0) * (y1 - y0) * 4];
            for (int y = y0; y < y1; y++) {
                for (int x = x0; x < x1; x++) {
                    int pixel = Marshal.ReadInt32(buffer.Address, y * buffer.RowBytes + x * 4);
                    int offset = ((y - y0) * (x1 - x0) + (x - x0)) * 4;
                    pixels[offset] = (byte)pixel;
                    pixels[offset + 1] = (byte)(pixel >> 8);
                    pixels[offset + 2] = (byte)(pixel >> 16);
                    pixels[offset + 3] = (byte)(pixel >> 24);
                }
            }
            return pixels;
        }

        static int ChangedPixels(byte[] before, byte[] after) {
            Assert.Equal(before.Length, after.Length);
            int different = 0;
            for (int i = 0; i < before.Length; i += 4) {
                if (before[i] != after[i] || before[i + 1] != after[i + 1] ||
                    before[i + 2] != after[i + 2] || before[i + 3] != after[i + 3]) {
                    different++;
                }
            }
            return different;
        }

        [Fact]
        public void KeyboardDegreesFollowTheKeyAndTheDegreeStyle() {
            HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                HeadlessUi.Errors.Clear();
                var window = new MainWindow { Width = 1280, Height = 800 };
                int oldDegreeStyle = Preferences.Default.DegreeStyle;
                try {
                    window.Show();
                    window.InitProject();
                    HeadlessUi.Flush();
                    // A project, through the welcome page, as the other piano roll tests make one.
                    var welcomeNew = FindButtonWithText(window, "welcome.new");
                    Click(window, welcomeNew.TranslatePoint(
                        new Point(welcomeNew.Bounds.Width / 2, welcomeNew.Bounds.Height / 2), window)!.Value);
                    var mainViewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
                    Assert.Equal(1, mainViewModel.Page);
                    var project = DocManager.Inst.Project;

                    // A part at the start of the first track, and the piano roll on it. The view model
                    // and the part notification are what the double click on a part passes to; the
                    // loading popup that handler raises does not finish under the headless timer.
                    var part = new UVoicePart { trackNo = 0, position = 0, Duration = 480 * 4 * 4 };
                    DocManager.Inst.StartUndoGroup("command.part.add");
                    DocManager.Inst.ExecuteCmd(new AddPartCommand(project, part));
                    DocManager.Inst.EndUndoGroup();
                    HeadlessUi.Flush();
                    var pianoRollViewModel = new PianoRollViewModel();
                    pianoRollViewModel.PlaybackViewModel = mainViewModel.PlaybackViewModel;
                    DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, project, 0));
                    var notesViewModel = pianoRollViewModel.NotesViewModel;
                    notesViewModel.TickWidth = 200.0 / 480.0;
                    notesViewModel.TickOffset = 0;
                    var pianoRoll = new PianoRoll(pianoRollViewModel) { MainWindow = window };
                    window.FindControl<ContentControl>("PianoRollContainer")!.Content = pianoRoll;
                    mainViewModel.ShowPianoRoll = true;
                    HeadlessUi.Flush();

                    // The keyboard is the left column of the piano roll.
                    var keyboard = pianoRoll.GetVisualDescendants()
                        .OfType<TrackBackground>().Single(background => background.IsKeyboard);
                    Assert.True(keyboard.Bounds.Width > 0 && keyboard.Bounds.Height > 0, "the keyboard has no size");
                    Assert.Equal(project.key, keyboard.Key);

                    // A key change from elsewhere - the main window's key menu, an undo - moves the
                    // degrees with it, on the view model and on the keyboard that draws them.
                    int key = project.key;
                    int otherKey = (key + 5) % 12;
                    DocManager.Inst.StartUndoGroup("command.project.key");
                    DocManager.Inst.ExecuteCmd(new KeyCommand(project, otherKey));
                    DocManager.Inst.EndUndoGroup();
                    HeadlessUi.Flush();
                    Assert.Equal(otherKey, notesViewModel.Key);
                    Assert.Equal(otherKey, keyboard.Key);
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                    Assert.Equal(key, notesViewModel.Key);
                    Assert.Equal(key, keyboard.Key);

                    // Switching the degree style on redraws the keyboard. The preference change alone
                    // does not reach it: what redraws it is the announcement the menus send.
                    Preferences.Default.DegreeStyle = 0;
                    MessageBus.Current.SendMessage(new PianorollRefreshEvent("DegreeStyle"));
                    HeadlessUi.Flush();
                    var withoutDegrees = CaptureControl(window, keyboard);
                    Preferences.Default.DegreeStyle = 1;
                    HeadlessUi.Flush();
                    Assert.Equal(0, ChangedPixels(withoutDegrees, CaptureControl(window, keyboard)));
                    MessageBus.Current.SendMessage(new PianorollRefreshEvent("DegreeStyle"));
                    HeadlessUi.Flush();
                    var withDegrees = CaptureControl(window, keyboard);
                    Assert.True(ChangedPixels(withoutDegrees, withDegrees) > 0,
                        "The keyboard did not redraw when the degree style changed");

                    Assert.Equal(1, Preferences.Default.DegreeStyle);

                    // Nothing else went wrong opening the roll, apart from the search bar's bindings,
                    // which wait for the view model the search panel installs (the app logs those too).
                    Assert.All(HeadlessUi.Errors.Snapshot(),
                        error => Assert.Contains(nameof(SearchNoteViewModel), error));
                } finally {
                    Preferences.Default.DegreeStyle = oldDegreeStyle;
                    // Leave the shared document as the test found it.
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                    HeadlessUi.SaveScreenshot(window, nameof(KeyboardDegreesFollowTheKeyAndTheDegreeStyle));
                    window.Close();
                }
            });
        }
        /// <summary>
        /// The keyboard flag of a background control used to announce itself as the piano roll flag,
        /// so a binding to IsKeyboard never heard about it and one to IsPianoRoll heard about the
        /// wrong change. Both flags also decide what the background paints, so they each repaint.
        /// </summary>
        [Fact]
        public void SettingTheKeyboardFlagRaisesTheKeyboardProperty() {
            HeadlessUi.Run(() => {
                bool keyboardChanged = false;
                bool pianoRollChanged = false;
                using var keyboardSubscription =
                    TrackBackground.IsKeyboardProperty.Changed.Subscribe(_ => keyboardChanged = true);
                using var pianoRollSubscription =
                    TrackBackground.IsPianoRollProperty.Changed.Subscribe(_ => pianoRollChanged = true);
                var background = new TrackBackground();
                Assert.False(background.IsKeyboard);
                background.IsKeyboard = true;
                Assert.True(background.IsKeyboard);
                Assert.True(keyboardChanged, "IsKeyboard did not announce its own change");
                Assert.False(pianoRollChanged, "IsKeyboard announced a change of IsPianoRoll");
            });
        }
    }
}

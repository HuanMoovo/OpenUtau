using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

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
    /// The portrait behind the piano roll, end to end: a singer whose bank resolves to a real
    /// image loads it, the image is placed inside the note area instead of the canvas origin at
    /// 0,0, and the View > Show portrait refresh hides and reloads it right away.
    /// </summary>
    public class PortraitPipelineTest {
        /// <summary>A singer that resolves to a real png file, like a bank whose only image is its avatar.</summary>
        class PortraitSinger : USinger {
            readonly string portrait;
            // The decode can be held, so the test can look at the refreshes that arrive while
            // one load is still running.
            readonly ManualResetEventSlim gate = new ManualResetEventSlim(true);
            int loads;
            int portraitReads;

            public PortraitSinger(string portrait) {
                this.portrait = portrait;
                found = true;
            }

            public int Loads => Volatile.Read(ref loads);
            public int PortraitReads => Volatile.Read(ref portraitReads);
            public void Hold() => gate.Reset();
            public void Release() => gate.Set();

            public override string Id => "portrait test singer";
            public override string Name => "Portrait Test Singer";
            public override USingerType SingerType => USingerType.Classic;
            public override IList<string> SearchTerms { get; } = new List<string>();
            public override string Portrait {
                get {
                    Interlocked.Increment(ref portraitReads);
                    return portrait;
                }
            }
            public override float PortraitOpacity => 0.67f;
            public override byte[] LoadPortrait() {
                Interlocked.Increment(ref loads);
                gate.Wait(2000);
                return File.ReadAllBytes(portrait);
            }
        }

        static void Click(Window window, Control target) {
            var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window);
            Assert.NotNull(center);
            window.MouseDown(center.Value, MouseButton.Left);
            window.MouseUp(center.Value, MouseButton.Left);
            HeadlessUi.Flush();
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

        /// <summary>The portrait is decoded off the UI thread, so wait for it like the app does.</summary>
        static bool WaitFor(Func<bool> condition) {
            for (int i = 0; i < 500; i++) {
                HeadlessUi.Flush();
                if (condition()) {
                    return true;
                }
                Thread.Sleep(5);
            }
            return condition();
        }

        /// <summary>A real png on disk, encoded by the same platform the app loads portraits with.</summary>
        static void WritePng(string path, int size) {
            var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var frame = bitmap.Lock()) {
                for (int y = 0; y < size; y++) {
                    for (int x = 0; x < size; x++) {
                        Marshal.WriteInt32(frame.Address, y * frame.RowBytes + x * 4, unchecked((int)0xFF80FF00));
                    }
                }
            }
            bitmap.Save(path);
        }

        [Fact]
        public void PortraitLoadsIsPlacedAndFollowsTheShowPortraitRefresh() {
            HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                HeadlessUi.Errors.Clear();
                var window = new MainWindow { Width = 1280, Height = 800 };
                string dir = Path.Combine(Path.GetTempPath(), "OpenUtau-PortraitPipelineTest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var singer = new PortraitSinger(Path.Combine(dir, "avatar.png"));
                bool showPortrait = Preferences.Default.ShowPortrait;
                try {
                    window.Show();
                    window.InitProject();
                    HeadlessUi.Flush();
                    // A project, through the welcome page, as the other tests make one.
                    Click(window, FindButtonWithText(window, "welcome.new"));
                    var mainViewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
                    Assert.Equal(1, mainViewModel.Page);
                    var project = DocManager.Inst.Project;

                    // A part on the first track, whose singer resolves to a 24px png.
                    WritePng(singer.Portrait, 24);
                    var part = new UVoicePart { trackNo = 0, position = 0, Duration = 480 * 4 * 4 };
                    DocManager.Inst.StartUndoGroup("command.part.add");
                    DocManager.Inst.ExecuteCmd(new AddPartCommand(project, part));
                    DocManager.Inst.EndUndoGroup();
                    HeadlessUi.Flush();
                    project.tracks[0].Singer = singer;
                    Preferences.Default.ShowPortrait = true;

                    // Open the piano roll on it, the way its view models do.
                    var pianoRollViewModel = new PianoRollViewModel { PlaybackViewModel = mainViewModel.PlaybackViewModel };
                    DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, project, 0));
                    var notes = pianoRollViewModel.NotesViewModel;
                    Assert.Same(part, notes.Part);

                    Assert.True(WaitFor(() => notes.Portrait != null),
                        "The portrait never loaded: " + string.Join("; ", HeadlessUi.Errors.Snapshot()));
                    Assert.Equal(24.0, notes.Portrait!.Size.Width);
                    Assert.NotNull(notes.PortraitMask);

                    // The image is drawn in the note area, 100px from its right edge - not left at
                    // the canvas origin.
                    var pianoRoll = new PianoRoll(pianoRollViewModel) { MainWindow = window };
                    window.FindControl<ContentControl>("PianoRollContainer")!.Content = pianoRoll;
                    mainViewModel.ShowPianoRoll = true;
                    HeadlessUi.Flush();
                    var image = pianoRoll.FindControl<Image>("PortraitImage")!;
                    var canvas = pianoRoll.FindControl<Canvas>("PortraitCanvas")!;
                    Assert.True(canvas.Bounds.Width > 0, "The portrait canvas has no size");
                    Assert.Equal(24.0, image.Bounds.Width);
                    // The portrait must not take pointer input away from the note grid: the
                    // canvas and everything in it stays out of hit testing.
                    Assert.False(canvas.IsHitTestVisible);
                    Assert.Equal(canvas.Bounds.Width - image.Bounds.Width - 100, Canvas.GetLeft(image), 1);

                    // 视图 > 显示立绘 sends this refresh: hiding clears the portrait at once...
                    Preferences.Default.ShowPortrait = false;
                    MessageBus.Current.SendMessage(new PianorollRefreshEvent("Portrait"));
                    HeadlessUi.Flush();
                    Assert.Null(notes.Portrait);
                    // ...and showing it again loads it immediately.
                    Preferences.Default.ShowPortrait = true;
                    MessageBus.Current.SendMessage(new PianorollRefreshEvent("Portrait"));
                    Assert.True(WaitFor(() => notes.Portrait != null), "Showing the portrait again did not reload it");

                    // Refreshes that arrive while a decode is running - every part and note
                    // switch makes one - must not start it a second time. The singer holds the
                    // decode open while the same refresh is sent again and again.
                    //
                    // The refresh is a broadcast, and piano rolls left over from other tests
                    // resolve this same singer through the shared project, so first count how
                    // many of them there are: hiding the portrait makes each one read the
                    // portrait path exactly once, and starts none.
                    Preferences.Default.ShowPortrait = false;
                    int readsBefore = singer.PortraitReads;
                    MessageBus.Current.SendMessage(new PianorollRefreshEvent("Portrait"));
                    HeadlessUi.Flush();
                    int viewers = singer.PortraitReads - readsBefore;
                    Assert.True(viewers >= 1, "No live piano roll reacted to the refresh");
                    singer.Hold();
                    int loadsBefore = singer.Loads;
                    Preferences.Default.ShowPortrait = true;
                    MessageBus.Current.SendMessage(new PianorollRefreshEvent("Portrait"));
                    // Each of them reserves the key and decodes once - and only once.
                    Assert.True(WaitFor(() => singer.Loads == loadsBefore + viewers),
                        $"The reloads did not start: expected {viewers}, loads={singer.Loads - loadsBefore}");
                    for (int i = 0; i < 20; i++) {
                        MessageBus.Current.SendMessage(new PianorollRefreshEvent("Portrait"));
                    }
                    Assert.Equal(loadsBefore + viewers, singer.Loads);
                    singer.Release();
                    Assert.True(WaitFor(() => notes.Portrait != null), "The held portrait never finished loading");
                    // No queued duplicate started late either.
                    Assert.Equal(loadsBefore + viewers, singer.Loads);
                } finally {
                    singer.Release();
                    Preferences.Default.ShowPortrait = showPortrait;
                    // Leave the shared document as the test found it: the other headless tests click
                    // through the welcome page's new project flow, which stops to ask about unsaved changes.
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                    window.Close();
                    Directory.Delete(dir, true);
                }
            });
        }
    }
}

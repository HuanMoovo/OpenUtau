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
    /// The singer's model image in the track UI, end to end: the portrait pipeline resolves an
    /// image for the track's singer and the track UI shows it - the inspector panel as a wide
    /// banner, the compact track row as a thumbnail - with one decode per singer, shared by both,
    /// and nothing at all while the track has no singer. The piano roll no longer offers, or
    /// draws, a singer background image.
    /// </summary>
    public class SingerPortraitTrackTest {
        /// <summary>A singer that resolves to a real png file, like a bank whose only image is its avatar.</summary>
        class PortraitSinger : USinger {
            readonly string portrait;
            // The decode can be held, so the test can look at the refreshes that arrive while
            // one load is still running.
            readonly ManualResetEventSlim gate = new ManualResetEventSlim(true);
            int loads;

            public PortraitSinger(string portrait) {
                this.portrait = portrait;
                found = true;
            }

            public int Loads => Volatile.Read(ref loads);
            public void Hold() => gate.Reset();
            public void Release() => gate.Set();

            public override string Id => "portrait test singer";
            public override string Name => "Portrait Test Singer";
            public override USingerType SingerType => USingerType.Classic;
            public override IList<string> SearchTerms { get; } = new List<string>();
            public override string Portrait => portrait;
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

        /// <summary>The model image is decoded off the UI thread, so wait for it like the app does.</summary>
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

        static void AssertNoErrors() {
            var errors = HeadlessUi.Errors.Snapshot();
            Assert.True(errors.Count == 0, "Errors reported: " + string.Join(" | ", errors));
        }

        /// <summary>Opens a fresh project in a headless window, as the other UI tests do.</summary>
        static void WithNewProject(string name, Action<MainWindow, UProject> test) {
            HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                HeadlessUi.Errors.Clear();
                var window = new MainWindow { Width = 1280, Height = 800 };
                try {
                    window.Show();
                    window.InitProject();
                    HeadlessUi.Flush();
                    Click(window, FindButtonWithText(window, "welcome.new"));
                    var mainViewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
                    Assert.Equal(1, mainViewModel.Page);
                    test(window, DocManager.Inst.Project);
                } finally {
                    HeadlessUi.Flush();
                    window.Close();
                }
            });
        }

        static TrackHeader FirstRow(MainWindow window, UTrack track) =>
            window.GetVisualDescendants().OfType<TrackHeader>()
                .Single(header => header.Compact && header.ViewModel?.TrackNo == track.TrackNo + 1);

        static TrackHeader Inspector(MainWindow window) =>
            window.GetVisualDescendants().OfType<TrackHeader>().Single(header => !header.Compact);

        /// <summary>
        /// The model image of the track's singer shows in the inspector panel and in the compact
        /// track row; without a singer both show nothing; and the image behind both is decoded once
        /// per singer, never once per view or per refresh.
        /// </summary>
        [Fact]
        public void TheTrackUiShowsTheSingersModelImage() {
            WithNewProject(nameof(TheTrackUiShowsTheSingersModelImage), (window, project) => {
                string dir = Path.Combine(Path.GetTempPath(), "OpenUtau-SingerPortraitTest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var singer = new PortraitSinger(Path.Combine(dir, "portrait.png"));
                var second = new PortraitSinger(Path.Combine(dir, "portrait2.png"));
                WritePng(singer.Portrait, 24);
                WritePng(second.Portrait, 24);
                var track = project.tracks[0];
                var originalSinger = track.Singer;
                try {
                    var row = FirstRow(window, track);
                    var inspector = Inspector(window);
                    var banner = inspector.FindControl<Image>("PortraitBanner")!;
                    var thumb = row.FindControl<Image>("PortraitThumb")!;
                    Assert.NotNull(banner);
                    Assert.NotNull(thumb);

                    // A track without a singer shows no model image.
                    track.Singer = USinger.CreateMissing("no singer");
                    MessageBus.Current.SendMessage(new TracksRefreshEvent());
                    HeadlessUi.Flush();
                    Assert.Null(banner.Source);
                    Assert.Null(thumb.Source);
                    Assert.False(banner.IsEffectivelyVisible, "The banner shows without a singer");
                    Assert.False(thumb.IsEffectivelyVisible, "The thumbnail shows without a singer");

                    // With a singer, the model image appears in both views.
                    track.Singer = singer;
                    MessageBus.Current.SendMessage(new TracksRefreshEvent());
                    Assert.True(WaitFor(() => banner.Source != null && thumb.Source != null),
                        "The model image never loaded: " + string.Join("; ", HeadlessUi.Errors.Snapshot()));
                    HeadlessUi.Flush();
                    Assert.True(banner.IsEffectivelyVisible, "The inspector banner is not visible");
                    Assert.True(thumb.IsEffectivelyVisible, "The track row thumbnail is not visible");
                    // The banner is a wide strip in the 216px inspector column, and keeps the
                    // image's own shape (the test png is square).
                    Assert.True(banner.Bounds.Width > 0 && banner.Bounds.Height > 0, "The banner has no size");
                    Assert.True(banner.Bounds.Width <= 216,
                        $"The banner is wider than the inspector column: {banner.Bounds.Width}");
                    Assert.True(banner.Bounds.Height <= 150,
                        $"The banner is taller than its strip: {banner.Bounds.Height}");
                    Assert.Equal(banner.Bounds.Width, banner.Bounds.Height, 1);
                    // The row thumbnail is a small, avatar sized image.
                    Assert.Equal(30, thumb.Bounds.Height, 1);
                    Assert.Equal(30, thumb.Bounds.Width, 1);
                    // Both views draw the one bitmap: the image was decoded once, not once per view.
                    Assert.Same(banner.Source, thumb.Source);
                    Assert.Equal(1, singer.Loads);

                    // A refresh that arrives while a decode is running starts no second decode, and
                    // neither do the refreshes after it finished - for the new singer either.
                    second.Hold();
                    track.Singer = second;
                    MessageBus.Current.SendMessage(new TracksRefreshEvent());
                    Assert.True(WaitFor(() => second.Loads == 1), "The new singer's model image was never decoded");
                    // Until it has finished decoding there is nothing to show.
                    Assert.Null(banner.Source);
                    Assert.Null(thumb.Source);
                    for (int i = 0; i < 20; i++) {
                        MessageBus.Current.SendMessage(new TracksRefreshEvent());
                    }
                    HeadlessUi.Flush();
                    Assert.Equal(1, second.Loads);
                    Assert.Equal(1, singer.Loads);
                    second.Release();
                    Assert.True(WaitFor(() => banner.Source != null && thumb.Source != null),
                        "The held model image never finished loading");
                    // No queued duplicate started late either.
                    Assert.Equal(1, second.Loads);
                    Assert.Same(banner.Source, thumb.Source);

                    // Switching back to the first singer reuses its decoded image.
                    track.Singer = singer;
                    MessageBus.Current.SendMessage(new TracksRefreshEvent());
                    HeadlessUi.Flush();
                    Assert.NotNull(banner.Source);
                    Assert.NotNull(thumb.Source);
                    Assert.Equal(1, singer.Loads);
                    Assert.Equal(1, second.Loads);

                    // A picture of the result for CI: the banner under the inspector's title and
                    // the row thumbnail next to the track number.
                    HeadlessUi.SaveScreenshot(window, nameof(TheTrackUiShowsTheSingersModelImage));

                    AssertNoErrors();
                } finally {
                    singer.Release();
                    second.Release();
                    track.Singer = originalSinger;
                    MessageBus.Current.SendMessage(new TracksRefreshEvent());
                    HeadlessUi.Flush();
                    Directory.Delete(dir, true);
                }
            });
        }

        /// <summary>
        /// The piano roll no longer draws a singer image behind its notes, and its View menu - and
        /// the preferences page - no longer offer the removed setting.
        /// </summary>
        [Fact]
        public void ThePianoRollNoLongerShowsASingerBackgroundImage() {
            WithNewProject(nameof(ThePianoRollNoLongerShowsASingerBackgroundImage), (window, project) => {
                var mainViewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
                var pianoRollViewModel = new PianoRollViewModel { PlaybackViewModel = mainViewModel.PlaybackViewModel };
                DocManager.Inst.ExecuteCmd(new LoadPartNotification(
                    new UVoicePart { trackNo = 0, position = 0, Duration = 480 * 4 * 4 }, project, 0));
                var pianoRoll = new PianoRoll(pianoRollViewModel) { MainWindow = window };
                window.FindControl<ContentControl>("PianoRollContainer")!.Content = pianoRoll;
                mainViewModel.ShowPianoRoll = true;
                HeadlessUi.Flush();

                // Nothing is drawn behind the note grid any more.
                Assert.Null(pianoRoll.FindControl<Canvas>("PortraitCanvas"));
                Assert.Null(pianoRoll.FindControl<Image>("PortraitImage"));

                // The View menu no longer holds the removed item: the singer icon toggle, which
                // used to follow it, is now its first entry.
                var menu = pianoRoll.GetVisualDescendants().OfType<Menu>().Single();
                var viewHeader = window.FindResource("menu.view") as string;
                Assert.False(string.IsNullOrEmpty(viewHeader));
                var viewItem = menu.Items.OfType<MenuItem>()
                    .Single(item => item.Header as string == viewHeader);
                viewItem.Open();
                HeadlessUi.Flush();
                var headers = viewItem.Items.OfType<MenuItem>()
                    .Select(item => item.Header as string)
                    .ToList();
                var showIcon = window.FindResource("prefs.appearance.showicon") as string;
                Assert.False(string.IsNullOrEmpty(showIcon));
                Assert.Equal(showIcon, headers.First());
                Assert.DoesNotContain("Show portrait on piano roll", headers);
                Assert.DoesNotContain("在钢琴窗上显示歌手背景图", headers);
                // The resource keys behind it are gone too, so nothing can bring the item back.
                // FindResource answers UnsetValue for a key the app no longer defines.
                Assert.Equal(AvaloniaProperty.UnsetValue, window.FindResource("prefs.appearance.showportrait"));
                Assert.Equal(AvaloniaProperty.UnsetValue, window.FindResource("prefs.appearance.portraitheight"));
                Assert.Equal(AvaloniaProperty.UnsetValue, window.FindResource("prefs.appearance.portraitopacity"));

                // Nothing else went wrong opening the roll, apart from the search bar's bindings,
                // which wait for the view model the search panel installs (the app logs those too).
                Assert.All(HeadlessUi.Errors.Snapshot(),
                    error => Assert.Contains(nameof(SearchNoteViewModel), error));
            });
        }
    }
}

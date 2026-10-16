using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

using OpenUtau.App.Controls;
using OpenUtau.App.ViewModels;
using OpenUtau.App.Views;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

using Xunit;

namespace OpenUtau.UiTest {
    /// <summary>
    /// The editor's Synthesizer V Studio 2 style chrome: one toolbar row with the transport, the
    /// tempo, the time signature, the position and the piano roll's snap; the captions of the panels;
    /// the icon strip that shows and hides the track list, the voice column, the roll and its note
    /// properties, and the bottom hint strip, which prints the gestures of the surface above it and
    /// follows the modifiers that surface acts on.
    /// </summary>
    public class EditorChromeTest {
        static void AssertHasSize(Control? control, string? what = null) {
            Assert.NotNull(control);
            Assert.True(control!.IsEffectivelyVisible, $"{what ?? control.ToString()} is not visible");
            Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0, $"{what ?? control.ToString()} has no size");
        }

        static Button FindButtonWithText(Window window, string resourceKey) {
            var text = window.FindResource(resourceKey) as string;
            Assert.False(string.IsNullOrEmpty(text));
            var button = window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.IsEffectivelyVisible &&
                    b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text));
            Assert.NotNull(button);
            return button!;
        }

        static void Click(Window window, Control target) {
            var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window);
            Assert.NotNull(center);
            window.MouseDown(center!.Value, MouseButton.Left);
            window.MouseUp(center.Value, MouseButton.Left);
            HeadlessUi.Flush();
        }

        static MainWindowViewModel ViewModelOf(Window window) => Assert.IsType<MainWindowViewModel>(window.DataContext);

        static Grid EditorGridOf(Window window) => Assert.IsType<Grid>(window.FindControl<Grid>("EditorGrid"));

        static ToggleButton StripButton(Window window, string name) {
            var strip = Assert.IsType<Border>(window.FindControl<Border>("IconStrip"));
            var button = strip.FindControl<ToggleButton>(name);
            Assert.NotNull(button);
            return button!;
        }

        /// <summary>The strip button as the user sees it: present, square, with its tooltip.</summary>
        static void AssertStripButton(Window window, string name) {
            var button = StripButton(window, name);
            AssertHasSize(button, name);
            Assert.Equal(40, button.Bounds.Width);
            Assert.Equal(40, button.Bounds.Height);
            Assert.NotNull(button.GetValue(ToolTip.TipProperty));
        }

        static double LeftOf(Window window, Control control) =>
            control.TranslatePoint(new Point(0, 0), window)!.Value.X;

        static double RightOf(Window window, Control control) =>
            control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value.X;

        /// <summary>The four bars of the workspace: the arrangement's and the roll's own pair.</summary>
        static ScrollBar[] MusicScrollBars(Window window) => window.GetVisualDescendants().OfType<ScrollBar>()
            .Where(bar => bar.Classes.Contains("music")).ToArray();

        /// <summary>The cross size of a bar or of one of its parts: the thickness the user sees.</summary>
        static double Thickness(ScrollBar bar, Control part) =>
            bar.Orientation == Orientation.Vertical ? part.Bounds.Width : part.Bounds.Height;

        /// <summary>True while the control prints the string this resource key names.</summary>
        static bool ShowsResourceText(Control control, Window window, string key) =>
            control.GetVisualDescendants().OfType<TextBlock>()
                .Any(text => text.IsEffectivelyVisible && text.Text == window.FindResource(key) as string);

        /// <summary>
        /// Like <see cref="MainWindowTest.WithMainWindow"/>, but knows about the piano roll's search
        /// bar: its bindings wait for the SearchNoteViewModel the search panel installs when it is
        /// opened, so the harness reports them and the tests that open a roll allow exactly those.
        /// </summary>
        static void WithEditorWindow(string name, Action<MainWindow> test) {
            HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                HeadlessUi.Errors.Clear();
                var window = new MainWindow { Width = 1280, Height = 800 };
                try {
                    window.Show();
                    window.InitProject();
                    HeadlessUi.Flush();
                    test(window);
                    HeadlessUi.Flush();
                    Assert.All(HeadlessUi.Errors.Snapshot(),
                        error => Assert.Contains(nameof(SearchNoteViewModel), error));
                } finally {
                    HeadlessUi.Flush();
                    HeadlessUi.SaveScreenshot(window, name);
                    window.Close();
                }
            });
        }

        /// <summary>New project through the welcome page, so the editor page is the one on screen.</summary>
        static MainWindowViewModel OpenEditor(Window window) {
            Click(window, FindButtonWithText(window, "welcome.new"));
            var viewModel = ViewModelOf(window);
            Assert.Equal(1, viewModel.Page);
            return viewModel;
        }

        /// <summary>The hint chips the strip currently prints.</summary>
        static Border[] HintChips(Window window) => window.FindControl<Control>("HintBar")!
            .GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("hintChip")).ToArray();

        static string? GestureOf(Border chip) => chip.GetVisualDescendants().OfType<TextBlock>().First().Text;

        /// <summary>A part on the first track and the piano roll showing it, as the window attaches it.</summary>
        static PianoRoll AttachRoll(MainWindow window, MainWindowViewModel viewModel) {
            var project = DocManager.Inst.Project;
            var part = new UVoicePart { trackNo = 0, position = 0, Duration = 480 * 4 * 4 };
            DocManager.Inst.StartUndoGroup("command.part.add");
            DocManager.Inst.ExecuteCmd(new AddPartCommand(project, part));
            DocManager.Inst.EndUndoGroup();
            HeadlessUi.Flush();
            var rollViewModel = new PianoRollViewModel();
            rollViewModel.PlaybackViewModel = viewModel.PlaybackViewModel;
            DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, project, 0));
            var roll = new PianoRoll(rollViewModel) { MainWindow = window };
            window.AttachPianoRoll(roll);
            HeadlessUi.Flush();
            return roll;
        }

        [Fact]
        public void TheToolbarCarriesTheTransportTheReadoutsAndTheRollSnap() {
            WithEditorWindow(nameof(TheToolbarCarriesTheTransportTheReadoutsAndTheRollSnap), window => {
                var viewModel = OpenEditor(window);
                var toolbar = window.FindControl<Border>("EditorToolbar");
                AssertHasSize(toolbar, "the editor toolbar");
                foreach (var name in new[] {
                    "SeekStartButton", "PlayPauseButton", "PauseButton", "SeekEndButton", "LoopButton", "MetronomeButton" }) {
                    AssertHasSize(toolbar!.FindControl<Control>(name), name);
                }
                // The tempo and time signature the transport row prints are the project's.
                var texts = toolbar!.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
                Assert.Contains($"{viewModel.PlaybackViewModel.BeatPerBar}/{viewModel.PlaybackViewModel.BeatUnit}", texts);
                Assert.Contains($"{viewModel.PlaybackViewModel.Bpm:#0.00}", texts);
                // Without a piano roll there is no snap to show, and the toolbar says so.
                Assert.False(viewModel.HasRollNotes);
                Assert.False(toolbar.FindControl<ToggleButton>("ToolbarSnapToggle")!.IsEffectivelyVisible);

                // The transport buttons still fire: seek away, then back with the toolbar's own button.
                DocManager.Inst.ExecuteCmd(new SeekPlayPosTickNotification(480 * 4));
                HeadlessUi.Flush();
                Assert.Equal(480 * 4, DocManager.Inst.playPosTick);
                Click(window, toolbar.FindControl<Button>("SeekStartButton")!);
                Assert.Equal(0, DocManager.Inst.playPosTick);

                // The loop toggle drives the playback manager, and the roll brings the snap controls.
                var loop = toolbar.FindControl<ToggleButton>("LoopButton")!;
                var wasLooping = PlaybackManager.Inst.LoopPlayback;
                Click(window, loop);
                Assert.NotEqual(wasLooping, PlaybackManager.Inst.LoopPlayback);
                Click(window, loop);
                Assert.Equal(wasLooping, PlaybackManager.Inst.LoopPlayback);

                var roll = AttachRoll(window, viewModel);
                Assert.True(viewModel.HasRollNotes);
                var snapToggle = toolbar.FindControl<ToggleButton>("ToolbarSnapToggle")!;
                AssertHasSize(snapToggle, "the toolbar's snap toggle");
                var notesViewModel = roll.ViewModel.NotesViewModel;
                var wasSnapping = notesViewModel.IsSnapOn;
                Click(window, snapToggle);
                Assert.NotEqual(wasSnapping, notesViewModel.IsSnapOn);
                Assert.Equal(notesViewModel.IsSnapOn, viewModel.SnapOn);
                Click(window, snapToggle);
                Assert.Equal(wasSnapping, notesViewModel.IsSnapOn);

                // The toolbar's snap division menu opens the roll's own divisions.
                var divisions = toolbar.FindControl<Button>("ToolbarSnapDivButton")!;
                AssertHasSize(divisions, "the toolbar's snap division button");
                Assert.Equal(notesViewModel.SnapDivText, divisions.GetVisualDescendants().OfType<TextBlock>().First().Text);
                DocManager.Inst.Undo();
                HeadlessUi.Flush();
            });
        }

        [Fact]
        public void TheIconStripShowsAndHidesTheVoiceColumn() {
            WithEditorWindow(nameof(TheIconStripShowsAndHidesTheVoiceColumn), window => {
                var viewModel = OpenEditor(window);
                var (wasInspector, wasNoteParams) = (Preferences.Default.ShowInspector, Preferences.Default.ShowNoteParams);
                try {
                    var strip = window.FindControl<Border>("IconStrip");
                    AssertHasSize(strip, "the icon strip");
                    foreach (var name in new[] {
                        "StripTrackListButton", "StripVoiceButton", "StripPianoRollButton", "StripNoteParamsButton" }) {
                        AssertStripButton(window, name);
                    }
                    // The four glyphs come from one set: Material Design Icons paths drawn on the same
                    // 24px grid, so no button is larger or heavier than its neighbours.
                    var glyphs = strip!.GetVisualDescendants().OfType<Path>().ToList();
                    Assert.Equal(4, glyphs.Count);
                    Assert.All(glyphs, glyph => {
                        Assert.True(glyph.Classes.Contains("filled"), "a strip glyph is not a filled icon");
                        Assert.Equal(24, glyph.Width);
                        Assert.Equal(24, glyph.Height);
                        Assert.NotNull(glyph.Fill);
                    });
                    Assert.Single(glyphs.Select(glyph => (glyph.Width, glyph.Height)).Distinct());
                    var editor = window.FindControl<Grid>("EditorGrid")!;
                    var inspector = window.FindControl<Control>("InspectorBorder")!;
                    AssertHasSize(inspector, "the voice column");
                    Assert.Equal(216, editor.ColumnDefinitions[3].Width.Value);

                    Click(window, strip!.FindControl<ToggleButton>("StripVoiceButton")!);
                    Assert.False(inspector.IsEffectivelyVisible, "the icon strip does not hide the voice column");
                    Assert.Equal(0, editor.ColumnDefinitions[3].Width.Value);
                    Assert.False(Preferences.Default.ShowInspector);
                    Assert.False(viewModel.ShowInspector);

                    Click(window, strip.FindControl<ToggleButton>("StripVoiceButton")!);
                    AssertHasSize(inspector, "the voice column after the strip showed it again");
                    Assert.Equal(216, editor.ColumnDefinitions[3].Width.Value);
                    Assert.True(Preferences.Default.ShowInspector);

                    // The note properties button is the roll's own pane, through the same preference.
                    var noteParams = strip.FindControl<ToggleButton>("StripNoteParamsButton")!;
                    var wasOn = viewModel.ShowNoteParams;
                    Click(window, noteParams);
                    Assert.NotEqual(wasOn, viewModel.ShowNoteParams);
                    Click(window, noteParams);
                    Assert.Equal(wasOn, viewModel.ShowNoteParams);

                    // The roll button shows and hides the roll pane, and pulls a roll that lives in its
                    // own window into the editor instead of opening an empty pane.
                    var wasDetached = Preferences.Default.DetachPianoRoll;
                    try {
                        var roll = AttachRoll(window, viewModel);
                        var rollButton = strip.FindControl<ToggleButton>("StripPianoRollButton")!;
                        Assert.True(viewModel.ShowPianoRoll);
                        Click(window, rollButton);
                        Assert.False(viewModel.ShowPianoRoll);
                        Click(window, rollButton);
                        Assert.True(viewModel.ShowPianoRoll);

                        Preferences.Default.DetachPianoRoll = true;
                        viewModel.ShowPianoRoll = false;
                        HeadlessUi.Flush();
                        Click(window, rollButton);
                        Assert.True(viewModel.ShowPianoRoll, "the roll button left the pane empty");
                        Assert.False(Preferences.Default.DetachPianoRoll, "the roll stayed in its own window");
                        Assert.Same(roll, window.FindControl<ContentControl>("PianoRollContainer")!.Content);
                    } finally {
                        Preferences.Default.DetachPianoRoll = wasDetached;
                        DocManager.Inst.Undo();
                        HeadlessUi.Flush();
                    }
                } finally {
                    Preferences.Default.ShowInspector = wasInspector;
                    Preferences.Default.ShowNoteParams = wasNoteParams;
                    HeadlessUi.Flush();
                }
            });
        }

        [Fact]
        public void TheHintStripFollowsTheSurfaceAndTheModifiers() {
            WithEditorWindow(nameof(TheHintStripFollowsTheSurfaceAndTheModifiers), window => {
                var viewModel = ViewModelOf(window);
                var hintBar = window.FindControl<Control>("HintBar");
                Assert.NotNull(hintBar);
                // Home page: no editor, no hints.
                Assert.False(hintBar!.IsEffectivelyVisible, "the hint strip shows on the home page");
                OpenEditor(window);
                AssertHasSize(hintBar, "the hint strip");
                Assert.Same(MouseHints.Inst.Arrangement, viewModel.Hints);
                Assert.Equal(MouseHints.Inst.Arrangement.Count, HintChips(window).Length);
                // The panel above the strip names itself in its own caption bar; the strip must not
                // say that name a second time, and it never did for the roll either (see below).
                Assert.False(ShowsResourceText(hintBar, window, "panel.arrangement"),
                    "the hint strip repeats the arrangement's own caption");
                // The chips sit together at the strip's own left margin, always.
                var stripLeft = LeftOf(window, hintBar);
                var arrangementChips = HintChips(window);
                Assert.InRange(LeftOf(window, arrangementChips[0]), stripLeft, stripLeft + 24);
                for (int i = 1; i < arrangementChips.Length; i++) {
                    Assert.True(LeftOf(window, arrangementChips[i]) > LeftOf(window, arrangementChips[i - 1]),
                        "the hint chips are out of order");
                }

                try {
                    var roll = AttachRoll(window, viewModel);
                    // With the roll open the strip prints the roll's gestures.
                    Assert.Same(MouseHints.Inst.PianoRoll, viewModel.Hints);
                    var chips = HintChips(window);
                    Assert.Equal(MouseHints.Inst.PianoRoll.Count, chips.Length);
                    Assert.Contains(chips, chip => GestureOf(chip) == window.FindResource("hint.gesture.shiftwheel") as string);
                    Assert.False(ShowsResourceText(hintBar, window, "panel.pianoroll"),
                        "the hint strip repeats the piano roll's own caption");

                    // The hints follow the modifiers the roll's own input handling acts on.
                    var shiftChip = chips.First(chip => GestureOf(chip) == window.FindResource("hint.gesture.shiftwheel") as string);
                    Assert.False((shiftChip.DataContext as MouseHint)!.IsActive, "the wheel hint starts highlighted");
                    var canvas = roll.GetVisualDescendants().OfType<NotesCanvas>().First();
                    var center = canvas.TranslatePoint(new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2), window)!.Value;
                    window.MouseMove(center, RawInputModifiers.Shift);
                    HeadlessUi.Flush();
                    Assert.True((shiftChip.DataContext as MouseHint)!.IsActive, "holding shift does not highlight the shift hint");
                    Assert.True(shiftChip.Classes.Contains("active"), "the highlighted hint wears the accent");
                    window.MouseMove(center, RawInputModifiers.None);
                    HeadlessUi.Flush();
                    Assert.False((shiftChip.DataContext as MouseHint)!.IsActive);

                    // The screenshot of the modernised editor for the report.
                    HeadlessUi.SaveScreenshot(window, "ui-modernised");
                } finally {
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                }
            });
        }

        [Fact]
        public void TheModernisedEditorPicturesItselfInTheChosenDarkTheme() {
            WithEditorWindow(nameof(TheModernisedEditorPicturesItselfInTheChosenDarkTheme), window => {
                var viewModel = OpenEditor(window);
                var wasTheme = Preferences.Default.ThemeName;
                try {
                    var roll = AttachRoll(window, viewModel);
                    AssertHasSize(window.FindControl<ContentControl>("PianoRollContainer")!.Content as Control, "the piano roll");
                    // The test host carries its own preferences (light theme); the restyle is the
                    // app's black-gray palette, so the picture for the report is taken in it.
                    Preferences.Default.ThemeName = "Dark";
                    OpenUtau.App.App.SetTheme();
                    HeadlessUi.Flush();
                    Assert.True(OpenUtau.App.ThemeManager.IsDarkMode, "the dark theme did not load");
                    AssertHasSize(window.FindControl<Border>("EditorToolbar"), "the toolbar, dark theme");
                    AssertHasSize(window.FindControl<Border>("IconStrip"), "the icon strip, dark theme");
                    AssertHasSize(window.FindControl<Control>("HintBar"), "the hint strip, dark theme");
                    Assert.True(HeadlessUi.CountRenderedColors(window) > 10, "the window looks blank");
                    HeadlessUi.SaveScreenshot(window, "ui-modernised");
                } finally {
                    Preferences.Default.ThemeName = wasTheme;
                    OpenUtau.App.App.SetTheme();
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                }
            });
        }

        [Fact]
        public void TheTrackListButtonCollapsesTheColumnAndTheMenuKeepsItsRow() {
            WithEditorWindow(nameof(TheTrackListButtonCollapsesTheColumnAndTheMenuKeepsItsRow), window => {
                var viewModel = OpenEditor(window);
                var wasTrackList = Preferences.Default.ShowTrackList;
                try {
                    viewModel.ShowTrackList = true;
                    HeadlessUi.Flush();
                    var editor = EditorGridOf(window);
                    var menuBand = window.FindControl<Control>("MenuBand")!;
                    var header = window.FindControl<Control>("ArrangementHeader")!;
                    var bar = window.FindControl<ScrollBar>("HScrollBar")!;
                    var parts = window.FindControl<Control>("partsCanvas")!;
                    Assert.Equal(240, editor.ColumnDefinitions[0].Width.Value);
                    AssertHasSize(header, "the arrangement's caption bar");
                    AssertHasSize(bar, "the arrangement's scrollbar");
                    Assert.True(LeftOf(window, bar) >= RightOf(window, menuBand),
                        "the menu band is under the arrangement's scrollbar");

                    // The strip's first button folds the track list away and gives its room to the
                    // arrangement, which the bar and the parts canvas both follow.
                    Click(window, StripButton(window, "StripTrackListButton"));
                    Assert.Equal(0, editor.ColumnDefinitions[0].Width.Value);
                    Assert.True(header.Bounds.Width < 1, "the track list column still shows its caption");
                    Assert.False(Preferences.Default.ShowTrackList);
                    Assert.False(viewModel.ShowTrackList);
                    AssertHasSize(bar, "the arrangement's scrollbar, track list hidden");
                    Assert.True(LeftOf(window, bar) >= RightOf(window, menuBand),
                        "the collapsed column slid the arrangement's scrollbar under the menu");
                    // The arrangement itself starts at the window's left: the bar's row is the only
                    // one the menu band still shares, so the bar starts where the menu ends.
                    Assert.Equal(0, LeftOf(window, parts), 1);
                    Assert.True(LeftOf(window, bar) > LeftOf(window, parts),
                        "the menu band no longer holds the arrangement's scrollbar clear of itself");

                    Click(window, StripButton(window, "StripTrackListButton"));
                    Assert.Equal(240, editor.ColumnDefinitions[0].Width.Value);
                    AssertHasSize(header, "the caption bar after the strip showed the track list again");
                    Assert.True(Preferences.Default.ShowTrackList);
                    Assert.True(LeftOf(window, bar) >= RightOf(window, menuBand),
                        "the menu band is under the arrangement's scrollbar");
                    Assert.Equal(LeftOf(window, parts), LeftOf(window, bar), 1);
                } finally {
                    Preferences.Default.ShowTrackList = wasTrackList;
                    HeadlessUi.Flush();
                }
            });
        }

        [Fact]
        public void TheScrollBarsAreSlimAndKeepTheirRange() {
            WithEditorWindow(nameof(TheScrollBarsAreSlimAndKeepTheirRange), window => {
                var viewModel = OpenEditor(window);
                try {
                    var roll = AttachRoll(window, viewModel);
                    var bars = MusicScrollBars(window);
                    Assert.Equal(4, bars.Length);
                    // The bars of the workspace are the app's 10px bars now, and so is every other
                    // scroll bar: a plain ScrollViewer bar carries the same size.
                    Assert.All(bars, bar => Assert.Equal(10, Thickness(bar, bar)));
                    var plain = window.GetVisualDescendants().OfType<ScrollBar>()
                        .Where(bar => !bar.Classes.Contains("music")).ToList();
                    Assert.NotEmpty(plain);
                    Assert.All(plain.Where(bar => bar.Orientation == Orientation.Vertical),
                        bar => Assert.Equal(10, bar.Width));
                    Assert.All(plain.Where(bar => bar.Orientation == Orientation.Horizontal),
                        bar => Assert.Equal(10, bar.Height));
                    foreach (var bar in bars) {
                        // A quiet 4px thumb inside the 10px bar, never a hairline, never bulging out.
                        var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
                        Assert.True(Thickness(bar, thumb) <= Thickness(bar, bar), "the thumb bulges out of its bar");
                        Assert.InRange(Thickness(bar, thumb), 3, 6);
                        var length = bar.Orientation == Orientation.Vertical ? thumb.Bounds.Height : thumb.Bounds.Width;
                        Assert.True(length >= 20, $"the thumb is a {length:F0}px hairline");
                        // Fluent's bulky arrows are gone: a 2px stub, hidden arrow and all, so
                        // nothing of the 16px glyph can clip and nothing paints outside the bar.
                        var lines = bar.GetVisualDescendants().OfType<RepeatButton>()
                            .Where(button => button.Name == "PART_LineUpButton" || button.Name == "PART_LineDownButton")
                            .ToList();
                        Assert.Equal(2, lines.Count);
                        Assert.All(lines, line => {
                            var along = bar.Orientation == Orientation.Vertical ? line.Bounds.Height : line.Bounds.Width;
                            Assert.Equal(2, along);
                            Assert.True(Thickness(bar, line) <= Thickness(bar, bar), "a line button sticks out of its bar");
                            Assert.Equal(0, line.Opacity);
                            Assert.All(line.GetVisualDescendants().OfType<PathIcon>(),
                                icon => Assert.True(icon.Bounds.Width <= 16 && icon.Bounds.Height <= 16,
                                    "the arrow glyph grew past Fluent's own size"));
                        });
                    }

                    // The roll's own bar still shows the roll's range and still pages the roll.
                    var rollBar = roll.GetVisualDescendants().OfType<ScrollBar>().Single(bar => bar.Name == "HScrollBar");
                    var notes = roll.ViewModel.NotesViewModel;
                    Assert.Equal(notes.HScrollBarMax, rollBar.Maximum);
                    Assert.Equal(notes.ViewportTicks, rollBar.ViewportSize);
                    var offset = notes.TickOffset;
                    var pageDown = rollBar.GetVisualDescendants().OfType<RepeatButton>()
                        .Single(button => button.Name == "PART_PageDownButton");
                    Click(window, pageDown);
                    Assert.True(notes.TickOffset > offset, "clicking the roll's scrollbar track no longer pages");
                    Assert.Equal(notes.TickOffset, rollBar.Value);

                    // The arrangement's bar still shows the arrangement's range.
                    var arrangementBar = window.FindControl<ScrollBar>("HScrollBar")!;
                    var tracks = viewModel.TracksViewModel;
                    Assert.Equal(tracks.HScrollBarMax, arrangementBar.Maximum);
                    Assert.Equal(tracks.ViewportTicks, arrangementBar.ViewportSize);
                    Assert.Equal(tracks.TickOffset, arrangementBar.Value);

                    // And the track height scaler of the same 24px column still follows the tracks.
                    var scalers = window.GetVisualDescendants().OfType<ViewScaler>().ToList();
                    Assert.Equal(2, scalers.Count);
                    Assert.All(scalers, scaler => AssertHasSize(scaler, "the track height scaler"));
                    tracks.SetTrackHeight(tracks.TrackHeight);
                    HeadlessUi.Flush();
                    Assert.Equal(tracks.TrackHeight, scalers.Single(scaler => ReferenceEquals(scaler.DataContext, tracks)).Value);
                    Assert.Equal(notes.TrackHeight, scalers.Single(scaler => ReferenceEquals(scaler.DataContext, notes)).Value);
                } finally {
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                }
            });
        }

        /// <summary>
        /// The three pictures of the polished editor, for the owner: the workspace as it opens, the
        /// same workspace with the track list folded away and with the voice column hidden, so the
        /// icon strip's buttons can be seen working.
        /// </summary>
        [Fact]
        public void ThePolishedEditorPicturesItself() {
            WithEditorWindow(nameof(ThePolishedEditorPicturesItself), window => {
                var viewModel = OpenEditor(window);
                var wasTheme = Preferences.Default.ThemeName;
                var (wasInspector, wasTrackList) = (Preferences.Default.ShowInspector, Preferences.Default.ShowTrackList);
                try {
                    viewModel.ShowTrackList = true;
                    viewModel.ShowInspector = true;
                    AttachRoll(window, viewModel);
                    AssertHasSize(window.FindControl<ContentControl>("PianoRollContainer")!.Content as Control, "the piano roll");
                    AssertHasSize(window.FindControl<Control>("InspectorBorder"), "the voice column");
                    AssertHasSize(window.FindControl<Control>("ArrangementHeader"), "the track list caption");
                    Preferences.Default.ThemeName = "Dark";
                    OpenUtau.App.App.SetTheme();
                    HeadlessUi.Flush();
                    Assert.True(HeadlessUi.CountRenderedColors(window) > 10, "the window looks blank");
                    HeadlessUi.SaveScreenshot(window, "ui-polished");

                    // Track list folded away: the arrangement takes the room of the column.
                    Click(window, StripButton(window, "StripTrackListButton"));
                    Assert.Equal(0, EditorGridOf(window).ColumnDefinitions[0].Width.Value);
                    HeadlessUi.SaveScreenshot(window, "ui-polished-noleft");

                    // The voice column hidden instead, with the track list back.
                    viewModel.ShowTrackList = true;
                    viewModel.ShowInspector = false;
                    HeadlessUi.Flush();
                    Assert.Equal(240, EditorGridOf(window).ColumnDefinitions[0].Width.Value);
                    Assert.Equal(0, EditorGridOf(window).ColumnDefinitions[3].Width.Value);
                    HeadlessUi.SaveScreenshot(window, "ui-polished-noinspector");
                } finally {
                    Preferences.Default.ShowInspector = wasInspector;
                    Preferences.Default.ShowTrackList = wasTrackList;
                    Preferences.Default.ThemeName = wasTheme;
                    OpenUtau.App.App.SetTheme();
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                }
            });
        }

        [Fact]
        public void ThePanelCaptionsNameThePanelsAndTheHeaderAddsTracks() {
            WithEditorWindow(nameof(ThePanelCaptionsNameThePanelsAndTheHeaderAddsTracks), window => {
                var viewModel = OpenEditor(window);
                var project = DocManager.Inst.Project;
                static bool ShowsResource(Control control, Window window, string key) =>
                    control.GetVisualDescendants().OfType<TextBlock>()
                        .Any(text => text.IsEffectivelyVisible && text.Text == window.FindResource(key) as string);

                Assert.True(ShowsResource(window.FindControl<Control>("ArrangementHeader")!, window, "panel.arrangement"),
                    "the track list has no 编排 caption");
                Assert.True(ShowsResource(window.FindControl<Control>("InspectorBorder")!, window, "panel.voice"),
                    "the voice column has no 歌声 caption");
                AssertHasSize(window.FindControl<Button>("AddTrackButton"), "the track list's add button");

                try {
                    // The caption bar's plus adds a track, like the plus at the end of the list does.
                    var trackCount = project.tracks.Count;
                    Click(window, window.FindControl<Button>("AddTrackButton")!);
                    Assert.Equal(trackCount + 1, project.tracks.Count);

                    var roll = AttachRoll(window, viewModel);
                    Assert.True(ShowsResource(roll, window, "panel.pianoroll"), "the piano roll has no 钢琴卷帘 caption");
                    AssertHasSize(window.FindControl<ContentControl>("PianoRollContainer")!.Content as Control, "the piano roll");
                } finally {
                    // The piano roll attachment is not a document change; the part and the track are.
                    DocManager.Inst.Undo();
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                }
            });
        }
    }
}

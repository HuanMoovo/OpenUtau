using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using OpenUtau.App;
using OpenUtau.App.ViewModels;
using OpenUtau.App.Views;
using OpenUtau.Core;
using OpenUtau.Core.Util;
using Xunit;

namespace OpenUtau.UiTest {
    /// <summary>
    /// Issue #2434: the main window's global hotkeys come from the preferences, so a rebound
    /// gesture has to run the command, the old gesture has to stop running it, and the
    /// Preferences page has to refuse gestures that are invalid or already taken.
    /// </summary>
    public class GlobalHotkeyTest {
        static MainWindowViewModel ViewModelOf(Window window) =>
            Assert.IsType<MainWindowViewModel>(window.DataContext);

        /// <summary>
        /// These tests change the real preferences, so the in-memory table and the prefs file
        /// are put back afterwards.
        ///
        /// prefs.json is process-global, and the test classes run in parallel: while this test
        /// snapshots or restores the file, another class saves it (Preferences.Save, a whole
        /// File.WriteAllText) - the NotesViewModel other tests construct writes it a dozen times
        /// in a row. A read or write that lands on that moment fails with "the file is being used
        /// by another process" rather than reading a half-written file (a plain reader blocks
        /// writers and vice versa), so the file access waits the writer out instead of racing it.
        /// </summary>
        static void WithUnchangedPreferences(Action test) {
            var saved = new Dictionary<string, string>(Preferences.Default.GlobalHotkeys);
            string prefsPath = PathManager.Inst.PrefsFilePath;
            string? savedFile = ReadPrefsSnapshot(prefsPath);
            try {
                test();
            } finally {
                Preferences.Default.GlobalHotkeys.Clear();
                foreach (var (commandId, gesture) in saved) {
                    Preferences.Default.GlobalHotkeys[commandId] = gesture;
                }
                if (savedFile == null) {
                    PrefsFile(() => File.Delete(prefsPath));
                } else {
                    PrefsFile(() => File.WriteAllText(prefsPath, savedFile));
                }
            }
        }

        // How long the file access below waits for a save to finish. A save holds prefs.json for
        // well under a millisecond, so this budget does not exhaust in practice; when it does,
        // the file error is thrown rather than the collision being ignored.
        const int PrefsFileAttempts = 2000;
        const int PrefsFileRetryDelayMs = 5;

        /// <summary>The prefs file as it is now, or null when it does not exist.</summary>
        static string? ReadPrefsSnapshot(string prefsPath) =>
            PrefsFile(() => File.Exists(prefsPath) ? File.ReadAllText(prefsPath) : null);

        /// <summary>
        /// Does the file work, retrying while a concurrent save holds prefs.json ("the file is
        /// being used by another process"). The same waits out a reader, and a file that is
        /// renamed over mid-operation, which is an IOException too.
        /// </summary>
        static T PrefsFile<T>(Func<T> work) {
            for (int attempt = 0; ; attempt++) {
                try {
                    return work();
                } catch (IOException) when (attempt < PrefsFileAttempts) {
                    Thread.Sleep(PrefsFileRetryDelayMs);
                }
            }
        }

        static void PrefsFile(Action work) => PrefsFile(() => {
            work();
            return 0;
        });

        /// <summary>Binds a command to a gesture the way the Preferences page does.</summary>
        static void Rebind(string commandId, string gesture) {
            GlobalHotkeyCommands.SetGesture(commandId, gesture);
        }

        // Ctrl+N used to open the editor from the welcome page. After rebinding it to Ctrl+P
        // the old key must do nothing at all, and the new one must run the command.
        [Fact]
        public void ReboundGestureDispatchesAndOldGestureStops() {
            WithUnchangedPreferences(() => {
                // Baseline: the default table is today's bindings.
                MainWindowTest.WithMainWindow(nameof(ReboundGestureDispatchesAndOldGestureStops) + "Default", window => {
                    Assert.Equal(0, ViewModelOf(window).Page);
                    window.KeyPress(Key.N, RawInputModifiers.Control, PhysicalKey.N, null);
                    HeadlessUi.Flush();
                    Assert.Equal(1, ViewModelOf(window).Page);
                });

                Rebind(GlobalHotkeyCommands.NewProject, "Ctrl+P");

                MainWindowTest.WithMainWindow(nameof(ReboundGestureDispatchesAndOldGestureStops) + "Rebound", window => {
                    var viewModel = ViewModelOf(window);
                    Assert.Equal(0, viewModel.Page);

                    // The old gesture is gone: Ctrl+N no longer opens the editor.
                    window.KeyPress(Key.N, RawInputModifiers.Control, PhysicalKey.N, null);
                    HeadlessUi.Flush();
                    Assert.Equal(0, viewModel.Page);

                    // A gesture no command holds falls through and does nothing.
                    window.KeyPress(Key.Q, RawInputModifiers.Control, PhysicalKey.Q, null);
                    HeadlessUi.Flush();
                    Assert.Equal(0, viewModel.Page);

                    // The rebound gesture runs the command.
                    window.KeyPress(Key.P, RawInputModifiers.Control, PhysicalKey.P, null);
                    HeadlessUi.Flush();
                    Assert.Equal(1, viewModel.Page);

                    // And the menu item advertises the new gesture instead of the old one.
                    Assert.Equal(new KeyGesture(Key.P, KeyModifiers.Control),
                        viewModel.HotkeyGestures[GlobalHotkeyCommands.NewProject]);
                    var menuItem = window.GetLogicalDescendants().OfType<MenuItem>()
                        .FirstOrDefault(item => Equals(item.Header, window.FindResource("menu.file.new")));
                    Assert.NotNull(menuItem);
                    Assert.Equal(viewModel.HotkeyGestures[GlobalHotkeyCommands.NewProject], menuItem.InputGesture);
                    Assert.Equal(new KeyGesture(Key.O, KeyModifiers.Control),
                        viewModel.HotkeyGestures[GlobalHotkeyCommands.OpenProject]);

                    // The menu follows a rebind the next time it is opened, where
                    // OnMainMenuOpened refreshes the gestures.
                    Rebind(GlobalHotkeyCommands.NewProject, "Ctrl+K");
                    viewModel.RefreshHotkeyGestures();
                    HeadlessUi.Flush();
                    Assert.Equal(new KeyGesture(Key.K, KeyModifiers.Control),
                        viewModel.HotkeyGestures[GlobalHotkeyCommands.NewProject]);
                    Assert.Equal(viewModel.HotkeyGestures[GlobalHotkeyCommands.NewProject], menuItem.InputGesture);
                });
            });
        }

        // The Preferences page's editing rules: a gesture is only stored when it parses and no
        // other command holds it, duplicate and invalid text are rejected, reset restores the
        // default, and what is stored survives the prefs JSON round trip.
        [Fact]
        public void HotkeyBindingsRejectDuplicatesAndInvalidGestures() {
            WithUnchangedPreferences(() => HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                var viewModel = new PreferencesViewModel();
                var rows = viewModel.GlobalHotkeyRows;

                // One row per command, in the table's order, showing the default gestures.
                Assert.Equal(GlobalHotkeyCommands.All, rows.Select(row => row.CommandId).ToList());
                Assert.All(rows, row => Assert.Equal(GlobalHotkeyCommands.Default(row.CommandId), row.Gesture));
                Assert.All(rows, row => Assert.False(row.HasError));
                var saveRow = rows.Single(row => row.CommandId == GlobalHotkeyCommands.Save);
                var newProjectRow = rows.Single(row => row.CommandId == GlobalHotkeyCommands.NewProject);

                // A gesture another command holds is rejected: not stored, and the row says who
                // holds it. Dispatch keeps resolving Ctrl+N to new project, not to save.
                saveRow.Gesture = newProjectRow.Gesture;
                Assert.True(saveRow.HasError);
                Assert.Contains(newProjectRow.Label, saveRow.Error);
                Assert.False(Preferences.Default.GlobalHotkeys.ContainsKey(GlobalHotkeyCommands.Save));
                Assert.True(GlobalHotkeys.TryResolve(KeyModifiers.Control, Key.N, editorActive: true, out string owner));
                Assert.Equal(GlobalHotkeyCommands.NewProject, owner);

                // A gesture that is not a gesture is rejected the same way.
                saveRow.Gesture = "Ctrl+";
                Assert.True(saveRow.HasError);
                saveRow.Gesture = "NotAKey";
                Assert.True(saveRow.HasError);
                saveRow.Gesture = string.Empty;
                Assert.True(saveRow.HasError);
                Assert.False(Preferences.Default.GlobalHotkeys.ContainsKey(GlobalHotkeyCommands.Save));

                // A free gesture is accepted, stored in its canonical spelling, and dispatches.
                saveRow.Gesture = "ctrl+shift+u";
                Assert.False(saveRow.HasError);
                Assert.Equal("Ctrl+Shift+U", saveRow.Gesture);
                Assert.Equal("Ctrl+Shift+U", Preferences.Default.GlobalHotkeys[GlobalHotkeyCommands.Save]);
                Assert.True(GlobalHotkeys.TryResolve(KeyModifiers.Control | KeyModifiers.Shift, Key.U, editorActive: true, out string rebound));
                Assert.Equal(GlobalHotkeyCommands.Save, rebound);
                // The editor commands stay off the welcome page, where the menus are disabled.
                Assert.False(GlobalHotkeys.TryResolve(KeyModifiers.Control | KeyModifiers.Shift, Key.U, editorActive: false, out _));

                // Rejecting an edit keeps the last accepted gesture bound.
                saveRow.Gesture = newProjectRow.Gesture;
                Assert.True(saveRow.HasError);
                Assert.True(GlobalHotkeys.TryResolve(KeyModifiers.Control | KeyModifiers.Shift, Key.U, editorActive: true, out string unchanged));
                Assert.Equal(GlobalHotkeyCommands.Save, unchanged);

                // What the page stored survives the preferences JSON round trip.
                Preferences.Save();
                var reloaded = Json.Deserialize<Preferences.SerializablePreferences>(
                    File.ReadAllText(PathManager.Inst.PrefsFilePath));
                Assert.NotNull(reloaded);
                Assert.Equal("Ctrl+Shift+U", reloaded!.GlobalHotkeys[GlobalHotkeyCommands.Save]);
                Assert.Equal("Ctrl+Shift+U", GlobalHotkeyCommands.Bindings(reloaded.GlobalHotkeys)[GlobalHotkeyCommands.Save]);

                // Reset puts the default back and drops the override.
                saveRow.Reset();
                Assert.Equal(GlobalHotkeyCommands.Default(GlobalHotkeyCommands.Save), saveRow.Gesture);
                Assert.False(Preferences.Default.GlobalHotkeys.ContainsKey(GlobalHotkeyCommands.Save));
                Assert.True(GlobalHotkeys.TryResolve(KeyModifiers.Control, Key.S, editorActive: true, out string reset));
                Assert.Equal(GlobalHotkeyCommands.Save, reset);
            }));
        }

        // The page itself: one editable gesture per command, and a button that restores the
        // defaults. Also catches broken bindings on the page, which only show at runtime.
        [Fact]
        public void PreferencesPageEditsGestures() {
            WithUnchangedPreferences(() => HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                HeadlessUi.Errors.Clear();
                var dialog = new PreferencesDialog { DataContext = new PreferencesViewModel() };
                try {
                    dialog.Show();
                    HeadlessUi.Flush();

                    var drawer = dialog.FindControl<ListBox>("DrawerList");
                    Assert.NotNull(drawer);
                    var hotkeysItem = drawer.Items.OfType<ListBoxItem>()
                        .FirstOrDefault(item => Equals(item.Content, dialog.FindResource("prefs.hotkeys")));
                    Assert.NotNull(hotkeysItem);
                    drawer.SelectedIndex = drawer.Items.IndexOf(hotkeysItem);
                    HeadlessUi.Flush();

                    var viewModel = Assert.IsType<PreferencesViewModel>(dialog.DataContext);
                    var boxes = dialog.GetVisualDescendants().OfType<TextBox>()
                        .Where(box => viewModel.GlobalHotkeyRows.Any(row => row.Gesture == box.Text))
                        .ToList();
                    Assert.Equal(viewModel.GlobalHotkeyRows.Count, boxes.Count);
                    // Every label the page uses exists in the localisation.
                    foreach (string key in new[] {
                        "prefs.hotkeys", "prefs.hotkeys.scope", "prefs.hotkeys.description",
                        "prefs.hotkeys.placeholder", "prefs.hotkeys.resetall",
                        "prefs.hotkeys.error.invalid", "prefs.hotkeys.error.duplicate",
                    }) {
                        Assert.False(string.IsNullOrEmpty(dialog.FindResource(key) as string), $"missing string {key}");
                    }
                    var buttons = dialog.GetVisualDescendants().OfType<Button>()
                        .Select(button => button.Content)
                        .ToList();
                    Assert.Contains(dialog.FindResource("prefs.hotkeys.resetall"), buttons);

                    // Editing the row through the dialog's text box rebinds the command.
                    var saveRow = viewModel.GlobalHotkeyRows.Single(row => row.CommandId == GlobalHotkeyCommands.Save);
                    var saveBox = boxes.Single(box => box.Text == saveRow.Gesture);
                    saveBox.Text = "Ctrl+Shift+J";
                    HeadlessUi.Flush();
                    Assert.Equal("Ctrl+Shift+J", saveRow.Gesture);
                    Assert.False(saveRow.HasError);

                    var errors = HeadlessUi.Errors.Snapshot();
                    Assert.True(errors.Count == 0, "Errors reported:\n" + string.Join("\n", errors));
                } finally {
                    HeadlessUi.Flush();
                    dialog.Close();
                }
            }));
        }

        // A saved gesture that is not a gesture, or that no command holds, resolves to no
        // command at all, so the key press falls through to the previous behaviour.
        [Fact]
        public void UnknownAndUnboundGesturesFallThrough() {
            WithUnchangedPreferences(() => HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                var saved = new Dictionary<string, string> {
                    [GlobalHotkeyCommands.NewProject] = "Ctrl+",              // not a gesture
                    [GlobalHotkeyCommands.OpenProject] = "NoSuchKey",         // not a key
                    [GlobalHotkeyCommands.Save] = "Ctrl+Shift+U",             // a real rebind
                };
                Assert.False(GlobalHotkeys.TryResolve(saved, KeyModifiers.Control, Key.N, editorActive: true, out _));
                Assert.False(GlobalHotkeys.TryResolve(saved, KeyModifiers.Control, Key.O, editorActive: true, out _));
                // Ctrl+Q is bound to nothing at all.
                Assert.False(GlobalHotkeys.TryResolve(saved, KeyModifiers.Control, Key.Q, editorActive: true, out _));
                Assert.False(GlobalHotkeys.TryResolve(KeyModifiers.None, Key.F1, editorActive: true, out _));
                // Commands whose gesture is fine are unaffected by the broken ones.
                Assert.True(GlobalHotkeys.TryResolve(
                    saved, KeyModifiers.Control | KeyModifiers.Shift, Key.U, editorActive: true, out string save));
                Assert.Equal(GlobalHotkeyCommands.Save, save);
                Assert.True(GlobalHotkeys.TryResolve(saved, KeyModifiers.None, Key.F11, editorActive: true, out string fullScreen));
                Assert.Equal(GlobalHotkeyCommands.FullScreen, fullScreen);
                // An unknown command id is not a command, so it cannot fire ...
                Assert.False(GlobalHotkeys.TryResolve(
                    new Dictionary<string, string> { ["removedCommand"] = "Ctrl+Alt+Y" },
                    KeyModifiers.Control | KeyModifiers.Alt, Key.Y, editorActive: true, out _));
                // ... and it is dropped instead of being handed on as a binding.
                var bindings = GlobalHotkeyCommands.Bindings(
                    new Dictionary<string, string> { ["removedCommand"] = "Ctrl+Alt+Y" });
                Assert.Equal(GlobalHotkeyCommands.All.OrderBy(id => id), bindings.Keys.OrderBy(id => id));
                Assert.Equal("Ctrl+Y", bindings[GlobalHotkeyCommands.Redo]);
            }));
        }
    }
}

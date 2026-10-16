using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using OpenUtau.App.ViewModels;
using OpenUtau.App.Views;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

using Xunit;

namespace OpenUtau.UiTest {
    /// <summary>
    /// The singers dialog can delete an installed singer (#1960). The singer menu asks first;
    /// only a singer installed in OpenUtau's own singer folder loses its files, while a singer
    /// found anywhere else is removed from the list and keeps its files, so nothing the user
    /// keeps outside OpenUtau's storage is ever deleted.
    /// </summary>
    public class DeleteSingerTest {
        // A singer the test controls. The delete path reads only the list-facing members and
        // the location, so a fake is enough here (see also FolderSinger in YamlEditorTest).
        class FolderSinger : USinger {
            readonly string location;
            public FolderSinger(string name, string location) {
                Name = name;
                this.location = location;
                found = true;
                loaded = true;
            }
            public override string Id => Name;
            public override string Name { get; }
            public override string Location => location;
            public override USingerType SingerType => USingerType.Classic;
            public override IList<string> SearchTerms { get; } = new List<string>();
            public override IList<string> Errors { get; } = new List<string>();
            public override IList<USubbank> Subbanks { get; } = new List<USubbank>();
        }

        static void WaitFor(Func<bool> condition) {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < 5000) {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                HeadlessUi.Flush();
                Thread.Sleep(10);
            }
            Assert.True(condition(), "Timed out");
        }

        static MessageBox WaitForMessageBox(Window owner) {
            MessageBox? box = null;
            WaitFor(() => (box = owner.OwnedWindows.OfType<MessageBox>().FirstOrDefault(b => b.IsVisible)) != null);
            return box!;
        }

        static void WaitForNoMessageBox(Window owner) =>
            WaitFor(() => !owner.OwnedWindows.OfType<MessageBox>().Any(b => b.IsVisible));

        static string MessageBoxText(MessageBox box) =>
            string.Join("\n", box.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));

        // A message box button by its localized label, so the test survives rewording of the rest.
        static void ClickMessageBoxButton(Window owner, MessageBox box, string resourceKey) {
            var caption = owner.FindResource(resourceKey) as string;
            Assert.False(string.IsNullOrEmpty(caption));
            var button = box.GetLogicalButtons().Single(b => (string?)b.Content == caption);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HeadlessUi.Flush();
        }

        static void Click(Window window, Control target) {
            var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window);
            Assert.NotNull(center);
            window.MouseDown(center!.Value, Avalonia.Input.MouseButton.Left);
            window.MouseUp(center.Value, Avalonia.Input.MouseButton.Left);
            HeadlessUi.Flush();
        }

        // Opens the singer menu and clicks its delete item, the way a user does.
        static void ClickDeleteSinger(SingersDialog dialog) {
            var menuButton = dialog.GetVisualDescendants().OfType<Button>()
                .Single(button => button.ContextMenu?.Name == "SingerMenu");
            Click(dialog, menuButton);
            var deleteItem = dialog.GetVisualDescendants().OfType<MenuItem>()
                .Concat(menuButton.ContextMenu!.Items.OfType<MenuItem>())
                .FirstOrDefault(item => (string?)item.Header == (string?)dialog.FindResource("singers.delete"));
            Assert.NotNull(deleteItem);
            deleteItem!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            HeadlessUi.Flush();
        }

        // The dialog on a headless session, with the fake singer in the manager's list.
        static void WithSingersDialog(USinger singer, Action<SingersViewModel, SingersDialog> test) {
            HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                var manager = SingerManager.Inst;
                manager.SingerGroups.TryGetValue(USingerType.Classic, out var oldGroup);
                try {
                    // Registered after InitCore, which scans the singer folders and would drop a
                    // singer registered before it.
                    manager.Singers[singer.Id] = singer;
                    manager.SingerGroups[USingerType.Classic] = (oldGroup ?? new List<USinger>()).Concat(new[] { singer }).ToList();
                    HeadlessUi.Errors.Clear();
                    var viewModel = new SingersViewModel();
                    var dialog = new SingersDialog { DataContext = viewModel, Width = 1050, Height = 700 };
                    try {
                        dialog.Show();
                        HeadlessUi.Flush();
                        viewModel.Singer = singer;
                        HeadlessUi.Flush();
                        Assert.Contains(singer, viewModel.Singers);
                        test(viewModel, dialog);
                        HeadlessUi.Flush();
                        // Only the dialog's own binding noise is tolerated, as in YamlEditorTest.
                        Assert.All(HeadlessUi.Errors.Snapshot(), error => Assert.Contains("Binding", error));
                    } finally {
                        dialog.Close();
                    }
                } finally {
                    manager.Singers.Remove(singer.Id);
                    if (oldGroup == null) {
                        manager.SingerGroups.Remove(USingerType.Classic);
                    } else {
                        manager.SingerGroups[USingerType.Classic] = oldGroup;
                    }
                }
            });
        }

        // A singer folder as installing a voicebank produces it, for a test to keep or lose.
        static string CreateSingerFolder(string parent, string name) {
            var dir = Path.Combine(parent, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "character.txt"), "name: " + name + "\n");
            File.WriteAllText(Path.Combine(dir, "character.yaml"), "name: " + name + "\n");
            return dir;
        }

        [Fact]
        public void DeletingAnInstalledSingerAsksFirstThenDeletesItsFiles() {
            // Under OpenUtau's own singer folder, where installing a voicebank puts it: this is
            // the only place the delete action is allowed to delete from.
            var installed = CreateSingerFolder(PathManager.Inst.SingersPath, "DeleteSingerTest." + Guid.NewGuid().ToString("N"));
            try {
                var singer = new FolderSinger("installed", installed);
                WithSingersDialog(singer, (viewModel, dialog) => {
                    ClickDeleteSinger(dialog);

                    // The confirmation names the singer and says its files go; no is a no.
                    var prompt = WaitForMessageBox(dialog);
                    Assert.Contains(singer.Location, MessageBoxText(prompt));
                    ClickMessageBoxButton(dialog, prompt, "button.no");
                    WaitForNoMessageBox(dialog);
                    Assert.Contains(singer, viewModel.Singers);
                    Assert.True(Directory.Exists(installed));

                    ClickDeleteSinger(dialog);
                    prompt = WaitForMessageBox(dialog);
                    ClickMessageBoxButton(dialog, prompt, "button.yes");

                    // Gone from the list, from the manager, and from disk.
                    WaitFor(() => !viewModel.Singers.Contains(singer));
                    Assert.DoesNotContain(singer, viewModel.Singers);
                    Assert.False(SingerManager.Inst.Singers.ContainsKey(singer.Id));
                    Assert.DoesNotContain(singer, SingerManager.Inst.SingerGroups[USingerType.Classic]);
                    Assert.False(Directory.Exists(installed));
                });
            } finally {
                if (Directory.Exists(installed)) {
                    Directory.Delete(installed, true);
                }
            }
        }

        [Fact]
        public void DeletingASingerOutsideOpenUtauKeepsItsFiles() {
            // Anywhere else, here a temp folder like an additional singer folder: the singer is
            // removed from the list, and the app must not touch the files.
            var external = Path.Combine(Path.GetTempPath(), "OpenUtau.UiTest." + Guid.NewGuid().ToString("N"));
            var dir = CreateSingerFolder(external, "Singer");
            try {
                var singer = new FolderSinger("external", dir);
                WithSingersDialog(singer, (viewModel, dialog) => {
                    ClickDeleteSinger(dialog);

                    // The confirmation says nothing is deleted because the files are not OpenUtau's.
                    var prompt = WaitForMessageBox(dialog);
                    Assert.Contains(dir, MessageBoxText(prompt));
                    ClickMessageBoxButton(dialog, prompt, "button.yes");

                    // Out of the list, but still on disk, and the message box says so.
                    WaitFor(() => !viewModel.Singers.Contains(singer));
                    Assert.DoesNotContain(singer, viewModel.Singers);
                    Assert.False(SingerManager.Inst.Singers.ContainsKey(singer.Id));
                    var notice = WaitForMessageBox(dialog);
                    Assert.Contains(dir, MessageBoxText(notice));
                    ClickMessageBoxButton(dialog, notice, "button.ok");
                    WaitForNoMessageBox(dialog);
                    Assert.True(Directory.Exists(dir));
                    Assert.True(File.Exists(Path.Combine(dir, "character.yaml")));
                });
            } finally {
                if (Directory.Exists(external)) {
                    Directory.Delete(external, true);
                }
            }
        }
    }
}

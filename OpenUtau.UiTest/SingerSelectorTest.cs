using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

using OpenUtau.App;
using OpenUtau.App.Controls;
using OpenUtau.App.ViewModels;
using OpenUtau.App.Views;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

using Xunit;

namespace OpenUtau.UiTest {
    /// <summary>
    /// The compact singer menu (issue #2467): the selector the singer button had before the tile
    /// flyout, back as the other view of that button. It names each singer's type above its singers
    /// and loads no images, so it opens immediately, and each selector carries the way to the other.
    /// </summary>
    public class SingerSelectorTest {
        class FakeSinger : USinger {
            readonly string name;
            readonly USingerType type;
            public FakeSinger(string name, USingerType type) {
                this.name = name;
                this.type = type;
                found = true;
            }
            public override string Id => name;
            public override string Name => name;
            public override USingerType SingerType => type;
            public override IList<string> SearchTerms { get; } = new List<string>();
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

        static void Click(Window window, Control target) {
            var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window);
            Assert.NotNull(center);
            window.MouseDown(center!.Value, MouseButton.Left);
            window.MouseUp(center.Value, MouseButton.Left);
            HeadlessUi.Flush();
        }

        /// <summary>The menu item, of the menu that is open, whose header is exactly this text.</summary>
        static MenuItem MenuItemWithHeader(Window window, string header) {
            var item = window.GetVisualDescendants().OfType<MenuItem>()
                .FirstOrDefault(menuItem => menuItem.IsEffectivelyVisible && (menuItem.Header as string) == header);
            Assert.NotNull(item);
            return item!;
        }

        /// <summary>The header of every group of the built menu, in order.</summary>
        static List<string> GroupHeaders(TrackHeaderViewModel viewModel) =>
            viewModel.SingerMenuItems!.Select(item => item.Header ?? string.Empty).ToList();

        /// <summary>The singers listed under a group header of the built menu.</summary>
        static List<string> SingersUnder(TrackHeaderViewModel viewModel, string header) {
            var group = viewModel.SingerMenuItems!.Single(item => item.Header == header);
            return group.Items!.Select(item => (item.CommandParameter as USinger)?.LocalizedName ?? item.Header!).ToList();
        }

        [Fact]
        public void TheSingerButtonOpensTheCompactMenuWhenItIsSelected() {
            HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                HeadlessUi.Errors.Clear();
                var window = new MainWindow { Width = 1280, Height = 800 };
                var classic = new FakeSinger("Compact One", USingerType.Classic);
                var diffSinger = new FakeSinger("Compact Two", USingerType.DiffSinger);
                var manager = SingerManager.Inst;
                var (oldFavorites, oldRecents) = (Preferences.Default.FavoriteSingers, Preferences.Default.RecentSingers);
                manager.SingerGroups.TryGetValue(USingerType.Classic, out var oldClassic);
                manager.SingerGroups.TryGetValue(USingerType.DiffSinger, out var oldDiffSinger);
                bool oldCompact = SingerSelectorSetting.UseCompactMenu;
                try {
                    window.Show();
                    window.InitProject();
                    HeadlessUi.Flush();
                    Click(window, FindButtonWithText(window, "welcome.new"));
                    Assert.Equal(1, Assert.IsType<MainWindowViewModel>(window.DataContext).Page);

                    // Two singers of different types, one recent and one favourite, so the menu shows
                    // every part it can group by.
                    foreach (var singer in new[] { classic, diffSinger }) {
                        manager.Singers[singer.Id] = singer;
                    }
                    manager.SingerGroups[USingerType.Classic] = new List<USinger> { classic };
                    manager.SingerGroups[USingerType.DiffSinger] = new List<USinger> { diffSinger };
                    Preferences.Default.RecentSingers = new List<string> { classic.Id };
                    Preferences.Default.FavoriteSingers = new List<string> { diffSinger.Id };

                    // The inspector holds the full track header, whose singer button this is.
                    var header = window.GetVisualDescendants().OfType<TrackHeader>().Single(h => !h.Compact);
                    var singerButton = header.FindControl<Button>("SingerButton")!;
                    var viewModel = Assert.IsType<TrackHeaderViewModel>(header.ViewModel);

                    SingerSelectorSetting.UseCompactMenu = true;
                    Click(window, singerButton);

                    // The compact menu, and no tile flyout: no avatars were decoded for it.
                    Assert.Empty(window.GetVisualDescendants().OfType<SingerFlyout>());
                    var headers = GroupHeaders(viewModel);
                    Assert.Contains(ThemeManager.GetString("tracks.favorite"), headers);
                    Assert.Contains(nameof(USingerType.Classic), headers);
                    Assert.Contains(nameof(USingerType.DiffSinger), headers);
                    Assert.Contains(ThemeManager.GetString("tracks.installsinger"), headers);
                    Assert.Contains(ThemeManager.GetString("singers.refresh"), headers);
                    Assert.Contains(ThemeManager.GetString("tracks.singertiles"), headers);
                    // The recent singer flattens above the groups, the way the old menu had it.
                    Assert.Equal(classic.LocalizedName, headers.First());
                    // Every group carries its singers.
                    Assert.Equal(new[] { classic.LocalizedName },
                        SingersUnder(viewModel, nameof(USingerType.Classic)));
                    Assert.Equal(new[] { diffSinger.LocalizedName },
                        SingersUnder(viewModel, nameof(USingerType.DiffSinger)));
                    Assert.Equal(new[] { diffSinger.LocalizedName },
                        SingersUnder(viewModel, ThemeManager.GetString("tracks.favorite")));
                    // The menu on screen is that menu: its groups are real menu items.
                    var onScreen = window.GetVisualDescendants().OfType<MenuItem>()
                        .Where(item => item.IsEffectivelyVisible)
                        .Select(item => item.Header as string).ToList();
                    Assert.Contains(nameof(USingerType.Classic), onScreen);
                    Assert.Contains(nameof(USingerType.DiffSinger), onScreen);
                    Assert.Contains(ThemeManager.GetString("tracks.installsinger"), onScreen);

                    // A submenu carries the type's singers.
                    MenuItemWithHeader(window, nameof(USingerType.Classic)).IsSubMenuOpen = true;
                    HeadlessUi.Flush();
                    Assert.Contains(classic.LocalizedName, window.GetVisualDescendants().OfType<MenuItem>()
                        .Where(item => item.IsEffectivelyVisible)
                        .Select(item => item.Header as string));

                    // Choosing a singer selects it for the track, like the tile flyout does.
                    Click(window, MenuItemWithHeader(window, classic.LocalizedName));
                    Assert.Same(classic, DocManager.Inst.Project.tracks[0].Singer);

                    // Its last item hands the button back to the tile flyout.
                    Click(window, singerButton);
                    Click(window, MenuItemWithHeader(window, ThemeManager.GetString("tracks.singertiles")));
                    Assert.False(SingerSelectorSetting.UseCompactMenu);
                    Click(window, singerButton);
                    var flyout = window.GetVisualDescendants().OfType<SingerFlyout>().Single();
                    Assert.True(flyout.Bounds.Width > 0 && flyout.Bounds.Height > 0, "the tile flyout has no size");

                    // …whose footer button hands it back to the compact menu, right away.
                    Click(window, flyout.FindControl<Button>("CompactSelectorButton")!);
                    Assert.True(SingerSelectorSetting.UseCompactMenu);
                    Assert.Empty(window.GetVisualDescendants().OfType<SingerFlyout>());
                    Click(window, singerButton);
                    Assert.Contains(ThemeManager.GetString("tracks.installsinger"),
                        window.GetVisualDescendants().OfType<MenuItem>().Where(item => item.IsEffectivelyVisible)
                            .Select(item => item.Header as string));

                    var errors = HeadlessUi.Errors.Snapshot();
                    Assert.True(errors.Count == 0, "Errors reported:\n" + string.Join("\n", errors));
                } finally {
                    SingerSelectorSetting.UseCompactMenu = oldCompact;
                    foreach (var singer in new[] { classic, diffSinger }) {
                        manager.Singers.Remove(singer.Id);
                        Preferences.Default.RecentSingers.Remove(singer.Id);
                        Preferences.Default.FavoriteSingers.Remove(singer.Id);
                    }
                    if (oldClassic == null) {
                        manager.SingerGroups.Remove(USingerType.Classic);
                    } else {
                        manager.SingerGroups[USingerType.Classic] = oldClassic;
                    }
                    if (oldDiffSinger == null) {
                        manager.SingerGroups.Remove(USingerType.DiffSinger);
                    } else {
                        manager.SingerGroups[USingerType.DiffSinger] = oldDiffSinger;
                    }
                    (Preferences.Default.FavoriteSingers, Preferences.Default.RecentSingers) = (oldFavorites, oldRecents);
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                    HeadlessUi.SaveScreenshot(window, nameof(TheSingerButtonOpensTheCompactMenuWhenItIsSelected));
                    window.Close();
                }
            });
        }
    }
}

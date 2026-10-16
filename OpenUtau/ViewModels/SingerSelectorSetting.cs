namespace OpenUtau.App.ViewModels {
    /// <summary>
    /// Which singer selector the track header opens: the tile flyout, or the classic compact
    /// menu, which lists the singers under their type name and loads no images, so it opens
    /// immediately (issue #2467). A view choice, remembered while the app runs; each selector
    /// carries a way to switch to the other one.
    /// </summary>
    public static class SingerSelectorSetting {
        public static bool UseCompactMenu { get; set; }
    }
}

using System.Collections.Generic;

namespace OpenUtau.Core.Util {
    /// <summary>
    /// Command ids and default gestures of the main window's configurable global hotkeys
    /// (issue #2434).
    ///
    /// The persisted shape is deliberately plain:
    /// <see cref="Preferences.SerializablePreferences.GlobalHotkeys"/> maps a command id to a
    /// gesture string such as "Ctrl+Shift+P", which survives the existing preferences JSON
    /// round trip and stays readable in prefs.json. Only gestures the user changed are
    /// stored there; everything else keeps the default below.
    ///
    /// Saved entries that are missing, empty, or belong to a command that no longer exists
    /// fall back to the default. A saved gesture that is not a gesture at all is kept as it
    /// is: dispatch then finds no command for those keys and the key press falls through the
    /// way an unbound key always did, and the Preferences page shows the row as invalid.
    /// Parsing a gesture into keys lives in <c>OpenUtau.App.GlobalHotkeys</c>, which also
    /// owns the dispatch.
    /// </summary>
    public static class GlobalHotkeyCommands {
        // Command ids. These are the keys of the persisted map, so they must stay stable.
        public const string NewProject = "newProject";
        public const string OpenProject = "openProject";
        public const string Save = "save";
        public const string SaveAs = "saveAs";
        public const string Undo = "undo";
        public const string Redo = "redo";
        public const string FullScreen = "fullScreen";
        public const string Exit = "exit";

        /// <summary>Every configurable command, in the order the Preferences page lists them.</summary>
        public static readonly IReadOnlyList<string> All = new[] {
            NewProject, OpenProject, Save, SaveAs, Undo, Redo, FullScreen, Exit,
        };

        /// <summary>
        /// The gesture a command had before hotkeys became configurable: the exact keys the
        /// main window used to dispatch, with Cmd instead of Ctrl on macOS.
        /// </summary>
        public static string Default(string commandId) {
            string cmd = OS.IsMacOS() ? "Cmd" : "Ctrl";
            switch (commandId) {
                case NewProject: return $"{cmd}+N";
                case OpenProject: return $"{cmd}+O";
                case Save: return $"{cmd}+S";
                case SaveAs: return $"{cmd}+Shift+S";
                case Undo: return $"{cmd}+Z";
                case Redo: return $"{cmd}+Y";
                case FullScreen: return "F11";
                case Exit: return "Alt+F4";
                default: return string.Empty;
            }
        }

        public static bool IsDefault(string commandId, string gesture) =>
            Default(commandId) == gesture;

        /// <summary>The gesture of a command: the saved override, else the default.</summary>
        public static string Gesture(string commandId, IReadOnlyDictionary<string, string>? saved) {
            if (saved != null
                && saved.TryGetValue(commandId, out string? gesture)
                && !string.IsNullOrWhiteSpace(gesture)) {
                return gesture.Trim();
            }
            return Default(commandId);
        }

        /// <summary>Effective gesture table: saved gestures overlaid on the defaults.</summary>
        public static Dictionary<string, string> Bindings(IReadOnlyDictionary<string, string>? saved) {
            var bindings = new Dictionary<string, string>();
            foreach (string commandId in All) {
                bindings[commandId] = Gesture(commandId, saved);
            }
            return bindings;
        }

        /// <summary>
        /// Stores a changed gesture, or drops the override when the gesture is the default
        /// again, so prefs.json only carries what the user actually changed.
        /// </summary>
        public static void SetGesture(string commandId, string gesture) {
            var overrides = Preferences.Default.GlobalHotkeys;
            if (string.IsNullOrWhiteSpace(gesture) || IsDefault(commandId, gesture.Trim())) {
                overrides.Remove(commandId);
            } else {
                overrides[commandId] = gesture.Trim();
            }
            Preferences.Save();
        }

        /// <summary>Puts a command back on its default gesture.</summary>
        public static void ResetGesture(string commandId) {
            Preferences.Default.GlobalHotkeys.Remove(commandId);
            Preferences.Save();
        }

        /// <summary>Puts every command back on its default gesture.</summary>
        public static void ResetAll() {
            Preferences.Default.GlobalHotkeys.Clear();
            Preferences.Save();
        }

        /// <summary>
        /// Drops entries that are unknown, empty or not the default value, used when the
        /// preferences are loaded so the map only holds gestures that still mean something.
        /// </summary>
        public static Dictionary<string, string> Sanitize(IReadOnlyDictionary<string, string>? saved) {
            var overrides = new Dictionary<string, string>();
            if (saved != null) {
                foreach (string commandId in All) {
                    if (saved.TryGetValue(commandId, out string? gesture)
                        && !string.IsNullOrWhiteSpace(gesture)
                        && !IsDefault(commandId, gesture.Trim())) {
                        overrides[commandId] = gesture.Trim();
                    }
                }
            }
            return overrides;
        }
    }
}

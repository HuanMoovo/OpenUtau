using System;
using System.Collections.Generic;
using Avalonia.Input;
using OpenUtau.Core.Util;

namespace OpenUtau.App {
    /// <summary>Where a configurable hotkey command is dispatched from.</summary>
    public enum HotkeyScope {
        /// <summary>Any page of the main window, like F11 and Alt+F4.</summary>
        Global,
        /// <summary>Only while the editor page is shown, where the menu items are enabled.</summary>
        Editor,
    }

    /// <summary>
    /// Turns the gesture strings stored in the preferences (see
    /// <see cref="GlobalHotkeyCommands"/>) into key presses and back, and resolves a key
    /// press to the command that holds it. This is the single place that decides what a
    /// gesture means, so dispatch and the Preferences page's validation always agree.
    /// </summary>
    public static class GlobalHotkeys {
        static readonly char[] separators = new[] { '+' };

        static readonly Dictionary<string, KeyModifiers> modifierNames = new() {
            ["ctrl"] = KeyModifiers.Control,
            ["control"] = KeyModifiers.Control,
            ["shift"] = KeyModifiers.Shift,
            ["alt"] = KeyModifiers.Alt,
            ["meta"] = KeyModifiers.Meta,
            ["win"] = KeyModifiers.Meta,
            ["cmd"] = KeyModifiers.Meta,
            ["command"] = KeyModifiers.Meta,
            ["super"] = KeyModifiers.Meta,
        };

        // Modifier keys are not gestures on their own, only modifiers of one.
        static readonly HashSet<Key> modifierKeys = new() {
            Key.LeftShift, Key.RightShift,
            Key.LeftCtrl, Key.RightCtrl,
            Key.LeftAlt, Key.RightAlt,
            Key.LWin, Key.RWin,
        };

        /// <summary>
        /// Which page a command belongs to: the ones the main window always dispatched, and
        /// the editor-file commands the editor page dispatches.
        /// </summary>
        public static HotkeyScope ScopeOf(string commandId) {
            switch (commandId) {
                case GlobalHotkeyCommands.Save:
                case GlobalHotkeyCommands.SaveAs:
                case GlobalHotkeyCommands.Undo:
                case GlobalHotkeyCommands.Redo:
                    return HotkeyScope.Editor;
                default:
                    return HotkeyScope.Global;
            }
        }

        /// <summary>Existing menu label shown for the command in the Preferences list.</summary>
        public static string LabelKey(string commandId) {
            switch (commandId) {
                case GlobalHotkeyCommands.NewProject: return "menu.file.new";
                case GlobalHotkeyCommands.OpenProject: return "menu.file.open";
                case GlobalHotkeyCommands.Save: return "menu.file.save";
                case GlobalHotkeyCommands.SaveAs: return "menu.file.saveas";
                case GlobalHotkeyCommands.Undo: return "menu.edit.undo";
                case GlobalHotkeyCommands.Redo: return "menu.edit.redo";
                case GlobalHotkeyCommands.FullScreen: return "menu.tools.fullscreen";
                case GlobalHotkeyCommands.Exit: return "menu.file.exit";
                default: return string.Empty;
            }
        }

        /// <summary>
        /// Parses a gesture like "Ctrl+Shift+P", "F11" or "Cmd+n" into key and modifiers.
        /// Modifier names are case insensitive, so a hand-edited prefs.json works too.
        /// </summary>
        public static bool TryParse(string? gesture, out KeyModifiers modifiers, out Key key) {
            modifiers = KeyModifiers.None;
            key = Key.None;
            if (string.IsNullOrWhiteSpace(gesture)) {
                return false;
            }
            bool hasKey = false;
            foreach (string part in gesture.Split(separators, StringSplitOptions.TrimEntries)) {
                if (part.Length == 0) {
                    return false;
                }
                if (modifierNames.TryGetValue(part.ToLowerInvariant(), out KeyModifiers modifier)) {
                    // Modifiers must come before the key: "N+Ctrl" is not a gesture.
                    if (hasKey) {
                        return false;
                    }
                    modifiers |= modifier;
                    continue;
                }
                // Two keys is not a gesture either.
                if (hasKey || !TryParseKey(part, out key)) {
                    return false;
                }
                hasKey = true;
            }
            return hasKey;
        }

        static bool TryParseKey(string token, out Key key) {
            // The digit row is D0-D9 in the Key enum; accept "1" the way users write it.
            if (token.Length == 1 && token[0] >= '0' && token[0] <= '9') {
                token = "D" + token;
            }
            if (!Enum.TryParse(token, ignoreCase: true, out key)
                || key == Key.None
                || !Enum.IsDefined(key)
                || modifierKeys.Contains(key)) {
                key = Key.None;
                return false;
            }
            return true;
        }

        /// <summary>The canonical spelling of a gesture, which is what gets stored.</summary>
        public static string Format(KeyModifiers modifiers, Key key) {
            var parts = new List<string>(5);
            if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Cmd");
            if (key >= Key.D0 && key <= Key.D9) {
                parts.Add(((int)(key - Key.D0)).ToString());
            } else {
                parts.Add(key.ToString());
            }
            return string.Join("+", parts);
        }

        /// <summary>
        /// Finds the command bound to a key press. False means no command holds the gesture:
        /// the key press stays unhandled, exactly as an unbound key did before hotkeys were
        /// configurable. Commands of another scope than the one being shown cannot match.
        /// </summary>
        public static bool TryResolve(KeyModifiers modifiers, Key key, bool editorActive, out string commandId) =>
            TryResolve(Preferences.Default.GlobalHotkeys, modifiers, key, editorActive, out commandId);

        /// <summary>Same, against an explicit gesture table, for tests and validation.</summary>
        public static bool TryResolve(
            IReadOnlyDictionary<string, string>? saved,
            KeyModifiers modifiers,
            Key key,
            bool editorActive,
            out string commandId) {
            commandId = string.Empty;
            if (key == Key.None) {
                return false;
            }
            foreach (string candidate in GlobalHotkeyCommands.All) {
                if (ScopeOf(candidate) == HotkeyScope.Editor && !editorActive) {
                    continue;
                }
                string gesture = GlobalHotkeyCommands.Gesture(candidate, saved);
                if (TryParse(gesture, out KeyModifiers boundModifiers, out Key boundKey)
                    && boundModifiers == modifiers
                    && boundKey == key) {
                    commandId = candidate;
                    return true;
                }
            }
            return false;
        }

        /// <summary>The gesture to show in the menus for a command, or null when unbound.</summary>
        public static KeyGesture? MenuGesture(string commandId) =>
            TryParse(GlobalHotkeyCommands.Gesture(commandId, Preferences.Default.GlobalHotkeys),
                out KeyModifiers modifiers, out Key key)
                ? new KeyGesture(key, modifiers)
                : null;

        /// <summary>Every command's menu gesture, keyed by command id.</summary>
        public static Dictionary<string, KeyGesture?> MenuGestures() {
            var gestures = new Dictionary<string, KeyGesture?>();
            foreach (string commandId in GlobalHotkeyCommands.All) {
                gestures[commandId] = MenuGesture(commandId);
            }
            return gestures;
        }
    }
}

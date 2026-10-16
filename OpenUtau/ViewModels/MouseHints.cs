using System.Collections.Generic;
using Avalonia.Input;
using OpenUtau.Core;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace OpenUtau.App.ViewModels {
    /// <summary>
    /// One mouse gesture of the editor and what it does, as printed in the bottom hint strip.
    /// The gesture and the action are separate strings so the strip can set the keys in monospace
    /// and read like Synthesizer V's own status bar.
    /// </summary>
    public partial class MouseHint : ViewModelBase {
        /// <summary>Modifiers this gesture needs; <see cref="KeyModifiers.None"/> = not modifier driven.</summary>
        public KeyModifiers Modifiers { get; }
        public string Gesture { get; }
        public string Text { get; }

        /// <summary>True while <see cref="Modifiers"/> are the ones held down on the hint's surface.</summary>
        [Reactive] public partial bool IsActive { get; set; }

        public MouseHint(string gestureKey, string textKey, KeyModifiers modifiers = KeyModifiers.None) {
            Modifiers = modifiers;
            Gesture = ThemeManager.GetString(gestureKey);
            Text = ThemeManager.GetString(textKey);
        }

        /// <summary>Highlights the hint when the modifiers it names are held.</summary>
        internal void SetModifiers(KeyModifiers modifiers) {
            IsActive = Modifiers != KeyModifiers.None && Modifiers == modifiers;
        }
    }

    /// <summary>
    /// The hints the editor's bottom strip prints, one list per surface. They live next to the
    /// input handlers that implement them (PianoRoll and MainWindow) and are pushed the current
    /// <see cref="KeyModifiers"/> by those same handlers, so the strip can only ever say what the
    /// code under it actually does.
    /// </summary>
    public class MouseHints {
        public static MouseHints Inst { get; } = new MouseHints();

        /// <summary>Gestures of the piano roll's note canvas.</summary>
        public IReadOnlyList<MouseHint> PianoRoll { get; }
        /// <summary>Gestures of the arrangement's track list and timeline.</summary>
        public IReadOnlyList<MouseHint> Arrangement { get; }

        MouseHints() {
            PianoRoll = new List<MouseHint> {
                new MouseHint("hint.gesture.shiftwheel", "hint.action.hscroll", KeyModifiers.Shift),
                new MouseHint("hint.gesture.ctrlwheel", "hint.action.hzoom", KeyModifiers.Control),
                new MouseHint("hint.gesture.altwheel", "hint.action.vzoom", KeyModifiers.Alt),
                new MouseHint("hint.gesture.drag", "hint.action.boxselect"),
                new MouseHint("hint.gesture.pendrag", "hint.action.drawnote"),
                new MouseHint("hint.gesture.dblnote", "hint.action.editlyrics"),
            };
            Arrangement = new List<MouseHint> {
                new MouseHint("hint.gesture.shiftwheel", "hint.action.hscroll", KeyModifiers.Shift),
                new MouseHint("hint.gesture.ctrlwheel", "hint.action.hzoom", KeyModifiers.Control),
                new MouseHint("hint.gesture.altwheel", "hint.action.trackheight", KeyModifiers.Alt),
                new MouseHint("hint.gesture.ctrldrag", "hint.action.selectparts"),
                new MouseHint("hint.gesture.dblpart", "hint.action.openroll"),
            };
        }

        public void SetModifiers(IReadOnlyList<MouseHint> surface, KeyModifiers modifiers) {
            foreach (var hint in surface) {
                hint.SetModifiers(modifiers);
            }
        }
    }
}

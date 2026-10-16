using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using OpenUtau.App.ViewModels;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace OpenUtau.App.Controls {
    class TickBackground : TemplatedControl {
        private static readonly IDashStyle DashStyle = new ImmutableDashStyle(new double[] { 2, 4 }, 0);

        public static readonly DirectProperty<TickBackground, int> ResolutionProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, int>(
                nameof(Resolution),
                o => o.Resolution,
                (o, v) => o.Resolution = v);
        public static readonly DirectProperty<TickBackground, double> TickWidthProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, double>(
                nameof(TickWidth),
                o => o.TickWidth,
                (o, v) => o.TickWidth = v);
        public static readonly DirectProperty<TickBackground, double> TickOffsetProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, double>(
                nameof(TickOffset),
                o => o.TickOffset,
                (o, v) => o.TickOffset = v);
        public static readonly DirectProperty<TickBackground, int> TickOriginProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, int>(
                nameof(TickOrigin),
                o => o.TickOrigin,
                (o, v) => o.TickOrigin = v);
        public static readonly DirectProperty<TickBackground, int> SnapDivProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, int>(
                nameof(SnapDiv),
                o => o.SnapDiv,
                (o, v) => o.SnapDiv = v);
        public static readonly DirectProperty<TickBackground, IReadOnlyList<int>?> SnapLinesProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, IReadOnlyList<int>?>(
                nameof(SnapLines),
                o => o.SnapLines,
                (o, v) => o.SnapLines = v);
        public static readonly DirectProperty<TickBackground, bool> ShowBarProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, bool>(
                nameof(ShowBar),
                o => o.ShowBar,
                (o, v) => o.ShowBar = v);

        public int Resolution {
            get => _resolution;
            private set => SetAndRaise(ResolutionProperty, ref _resolution, value);
        }
        // Tick width in pixel.
        public double TickWidth {
            get => _tickWidth;
            private set => SetAndRaise(TickWidthProperty, ref _tickWidth, value);
        }
        public double TickOffset {
            get => _tickOffset;
            private set => SetAndRaise(TickOffsetProperty, ref _tickOffset, value);
        }
        public int TickOrigin {
            get => _tickOrigin;
            private set => SetAndRaise(TickOriginProperty, ref _tickOrigin, value);
        }
        public int SnapDiv {
            get => _snapDiv;
            set => SetAndRaise(SnapDivProperty, ref _snapDiv, value);
        }
        /// <summary>
        /// The bar/beat lines to draw, computed by the view model (see NotesViewModel.UpdateSnapLines)
        /// rather than produced here while painting. Ascending; the last entry is the first bar past
        /// the right edge, which snapping uses but the renderer does not draw.
        /// </summary>
        public IReadOnlyList<int>? SnapLines {
            get => _snapLines;
            set => SetAndRaise(SnapLinesProperty, ref _snapLines, value);
        }
        public bool ShowBar {
            get => _showBar;
            set => SetAndRaise(ShowBarProperty, ref _showBar, value);
        }

        private int _resolution = 480;
        private double _tickWidth;
        private double _tickOffset;
        private int _tickOrigin;
        private int _snapDiv;
        private IReadOnlyList<int>? _snapLines;
        private bool _showBar = true;

        private Pen penBar;
        private Pen penBeatUnit;
        private Pen penDanshed;

        public TickBackground() {
            penBar = new Pen(Foreground, 1);
            penBeatUnit = new Pen(Background, 1);
            penDanshed = new Pen(Background, 1) {
                DashStyle = DashStyle,
            };
            MessageBus.Current.Listen<ThemeChangedEvent>()
                .Subscribe(e => InvalidateVisual());
            MessageBus.Current.Listen<TimeAxisChangedEvent>()
                .Subscribe(e => InvalidateVisual());
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
            base.OnPropertyChanged(change);
            if (change.Property == ForegroundProperty) {
                penBar = new Pen(Foreground, 1);
            }
            if (change.Property == BackgroundProperty) {
                penBeatUnit = new Pen(Background, 1);
                penDanshed = new Pen(Background, 1) {
                    DashStyle = DashStyle,
                };
            }
            if (change.Property == ResolutionProperty ||
                change.Property == TickOriginProperty ||
                change.Property == TickWidthProperty ||
                change.Property == TickOffsetProperty ||
                change.Property == SnapDivProperty ||
                change.Property == SnapLinesProperty ||
                change.Property == ShowBarProperty) {
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context) {
            if (TickWidth <= 0) {
                return;
            }
            var project = Core.DocManager.Inst.Project;
            // The lines come from the view model, which computed them from the same zoom, scroll,
            // bounds and time axis (see NotesViewModel.UpdateSnapLines / TracksViewModel.UpdateSnapLines).
            // That keeps snapping working before this control has ever painted.
            var snapLines = SnapLines;
            if (snapLines == null || snapLines.Count < 2) {
                return;
            }
            double pixelOffset = (TickOffset + TickOrigin) * TickWidth;
            // The last line is the bar past the right edge: snapping needs it, drawing never showed it.
            for (int i = 0; i < snapLines.Count - 1; i++) {
                int tick = snapLines[i];
                double x = Math.Round(tick * TickWidth - pixelOffset) + 0.5;
                project.timeAxis.TickPosToBarBeat(tick, out int bar, out int beat, out int remainingTicks);
                if (remainingTicks == 0 && beat == 0) {
                    double y = -0.5;
                    if (ShowBar) {
                        var textLayout = TextLayoutCache.Get((bar + 1).ToString(), ThemeManager.BarNumberBrush, 10);
                        using (var state = context.PushTransform(Matrix.CreateTranslation(x + 3, 10))) {
                            textLayout.Draw(context, new Point());
                        }
                    }
                    context.DrawLine(penBar, new Point(x, y), new Point(x, Bounds.Height + 0.5f));
                } else {
                    // Lines between bars.
                    var pen = remainingTicks != 0 ? penDanshed : penBeatUnit;
                    double y = ShowBar ? 24 : 0;
                    context.DrawLine(pen, new Point(x, y), new Point(x, Bounds.Height + 0.5f));
                }
            }

            if (ShowBar) {
                foreach (var tempo in project.tempos) {
                    double x = Math.Round(tempo.position * TickWidth - pixelOffset) + 0.5;
                    context.DrawLine(penDanshed, new Point(x, 0), new Point(x, 24));
                    var textLayout = TextLayoutCache.Get(tempo.bpm.ToString("#0.00"), ThemeManager.BarNumberBrush, 10);
                    using (var state = context.PushTransform(Matrix.CreateTranslation(x + 3, 0))) {
                        textLayout.Draw(context, new Point());
                    }
                }

                foreach (var timeSig in project.timeSignatures) {
                    int tick = project.timeAxis.BarBeatToTickPos(timeSig.barPosition, 0);
                    var barTextLayout = TextLayoutCache.Get((timeSig.barPosition + 1).ToString(), ThemeManager.BarNumberBrush, 10);
                    double x = Math.Round(tick * TickWidth - pixelOffset) + 0.5 + barTextLayout.Width + 4;
                    var textLayout = TextLayoutCache.Get($"{timeSig.beatPerBar}/{timeSig.beatUnit}", ThemeManager.BarNumberBrush, 10);
                    using (var state = context.PushTransform(Matrix.CreateTranslation(x + 3, 10))) {
                        textLayout.Draw(context, new Point());
                    }
                }
            }
        }
    }
}

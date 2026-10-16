using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenUtau.App.ViewModels;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using ReactiveUI;

namespace OpenUtau.App.Controls {
    public partial class TrackHeader : UserControl, IDisposable {
        public static readonly DirectProperty<TrackHeader, double> TrackHeightProperty =
            AvaloniaProperty.RegisterDirect<TrackHeader, double>(
                nameof(TrackHeight),
                o => o.TrackHeight,
                (o, v) => o.TrackHeight = v);
        public static readonly DirectProperty<TrackHeader, Point> OffsetProperty =
            AvaloniaProperty.RegisterDirect<TrackHeader, Point>(
                nameof(Offset),
                o => o.Offset,
                (o, v) => o.Offset = v);
        public static readonly DirectProperty<TrackHeader, int> TrackNoProperty =
            AvaloniaProperty.RegisterDirect<TrackHeader, int>(
                nameof(TrackNo),
                o => o.TrackNo,
                (o, v) => o.TrackNo = v);
        public static readonly DirectProperty<TrackHeader, bool> CompactProperty =
            AvaloniaProperty.RegisterDirect<TrackHeader, bool>(
                nameof(Compact),
                o => o.Compact,
                (o, v) => o.Compact = v);

        public double TrackHeight {
            get => trackHeight;
            set => SetAndRaise(TrackHeightProperty, ref trackHeight, value);
        }
        public Point Offset {
            get => offset;
            set => SetAndRaise(OffsetProperty, ref offset, value);
        }
        public int TrackNo {
            get => trackNo;
            set => SetAndRaise(TrackNoProperty, ref trackNo, value);
        }
        /// <summary>
        /// A compact header is one row of the track list: colour chip, name, mute and solo.
        /// A full one is the track inspector panel, which holds the singer, phonemizer, renderer,
        /// volume and pan controls.
        /// </summary>
        public bool Compact {
            get => compact;
            set {
                if (SetAndRaise(CompactProperty, ref compact, value)) {
                    UpdateVisibility();
                }
            }
        }
        /// <summary>
        /// The singer portrait doubles as the track reorder handle, so the inspector turns it off.
        /// </summary>
        public bool ShowAvatar {
            get => showAvatar;
            set {
                if (showAvatar != value) {
                    showAvatar = value;
                    UpdateVisibility();
                }
            }
        }

        private double trackHeight;
        private Point offset;
        private int trackNo;
        private bool compact;
        private bool showAvatar = true;
        private readonly KeyModifiers cmdKey =
            OS.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        public TrackHeaderViewModel? ViewModel;

        private List<IDisposable> unbinds = new List<IDisposable>();

        private UTrack? track;
        private TrackHeaderCanvas? canvas;

        public TrackHeader() {
            InitializeComponent();
            UpdateVisibility();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
            base.OnPropertyChanged(change);
            if (change.Property == OffsetProperty ||
                change.Property == TrackNoProperty ||
                change.Property == TrackHeightProperty) {
                SetPosition();
            }
        }

        internal void Bind(UTrack track, TrackHeaderCanvas? canvas = null) {
            this.track = track;
            this.canvas = canvas;
            if (canvas != null) {
                unbinds.Add(this.Bind(TrackHeightProperty, canvas.GetObservable(TrackHeaderCanvas.TrackHeightProperty)));
                unbinds.Add(this.Bind(HeightProperty, canvas.GetObservable(TrackHeaderCanvas.TrackHeightProperty)));
                unbinds.Add(this.Bind(OffsetProperty, canvas.WhenAnyValue(x => x.TrackOffset, trackOffset => new Point(0, -trackOffset * TrackHeight))));
            }
            SetPosition();
        }

        private void SetPosition() {
            Canvas.SetLeft(this, 0);
            Canvas.SetTop(this, Offset.Y + (track?.TrackNo ?? 0) * trackHeight);
            UpdateVisibility();
        }

        /// <summary>
        /// Shows the rows this header is meant to show. List rows keep publishing the row flags to the
        /// view model, because that is what the "⋯" overflow flyout keys off; the inspector reads none
        /// of them, since a track has a single view model that the list row and the inspector share.
        /// </summary>
        public void UpdateVisibility() {
            bool inList = canvas != null;
            // The inspector has no track height to fit into, so it shows every row.
            bool singer = !Compact && (!inList || trackHeight >= ViewConstants.TrackHeightDelta * 3);
            bool phonemizer = !Compact && (!inList || trackHeight >= ViewConstants.TrackHeightDelta * 4);
            bool renderer = !Compact && (!inList || trackHeight >= ViewConstants.TrackHeightDelta * 5);
            if (inList && ViewModel != null) {
                ViewModel.IsSingerVisible = singer;
                ViewModel.IsPhonemizerVisible = phonemizer;
                ViewModel.IsRendererVisible = renderer;
                ViewModel.AvatarHeight = Math.Min(trackHeight - (ViewConstants.TrackHeightDefault - 100), 100);
            }
            SingerButton.IsVisible = singer;
            PhonemizerButton.IsVisible = phonemizer;
            RendererButton.IsVisible = renderer;
            SettingsButton.IsVisible = renderer;
            // Only list rows fold the properties into the overflow flyout; the inspector shows them.
            MoreButton.IsVisible = inList;
            VolumePanRow.IsVisible = !Compact;
            MixFxButton.IsVisible = !Compact;
            AvatarBorder.IsVisible = !Compact && showAvatar;
            // The singer's model image: a banner in the inspector panel, a thumbnail in a row.
            PortraitBannerBorder.IsVisible = !Compact;
            PortraitThumbBorder.IsVisible = Compact;
            // The group headings belong to the inspector panel; a list row is too small for them.
            SingerHeading.IsVisible = !Compact;
            PronunciationHeading.IsVisible = !Compact;
            MixHeading.IsVisible = !Compact;
        }

        void HeaderPointerPressed(object? sender, PointerPressedEventArgs args) {
            if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
                track == null ||
                canvas?.DataContext is not TracksViewModel tracksViewModel) {
                return;
            }
            if (args.KeyModifiers == KeyModifiers.Shift) {
                tracksViewModel.SelectTracksUntil(track);
            } else if (args.KeyModifiers == cmdKey) {
                tracksViewModel.ToggleSelectTrack(track);
            } else {
                tracksViewModel.SelectTrack(track);
            }
        }

        void TrackNameButtonClicked(object sender, RoutedEventArgs args) {
            ViewModel?.Rename();
            args.Handled = true;
        }

        void SingerButtonClicked(object sender, RoutedEventArgs args) {
            args.Handled = true;
            ShowSingerFlyout(sender);
        }

        void SingerButtonContextRequested(object sender, ContextRequestedEventArgs args) {
            args.Handled = true;
            ShowSingerFlyout(sender);
        }

        void ShowSingerFlyout(object sender) {
            if (ViewModel == null) {
                return;
            }
            try {
                Control anchor = SingerButton;
                if (sender != SingerButton) {
                    // Opened from the "⋯" flyout: close it and anchor to its button instead of stacking flyouts.
                    MoreButton.Flyout?.Hide();
                    anchor = MoreButton;
                }
                if (SingerSelectorSetting.UseCompactMenu) {
                    // The classic selector (issue #2467): grouped by singer type, nothing to load.
                    ViewModel.RefreshSingerMenu();
                    if (this.FindResource("SingersMenuRes") is ContextMenu menu) {
                        menu.Open(anchor);
                    }
                    return;
                }
                var viewModel = new SingerFlyoutViewModel(() => ViewModel.Singer, ViewModel.SelectSingerCommand);
                var content = new SingerFlyout() { DataContext = viewModel };
                var flyout = new Flyout() {
                    Content = content,
                    Placement = PlacementMode.BottomEdgeAlignedLeft,
                    ShowMode = FlyoutShowMode.Standard,
                };
                flyout.FlyoutPresenterClasses.Add("singerFlyout");
                viewModel.CloseRequested += flyout.Hide;
                flyout.Opened += (_, _) => DocManager.Inst.AddSubscriber(viewModel);
                flyout.Closed += (_, _) => DocManager.Inst.RemoveSubscriber(viewModel);
                content.FitToScreen(anchor);
                flyout.ShowAt(anchor);
            } catch (Exception e) {
                DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(e));
            }
        }

        void PhonemizerButtonClicked(object sender, RoutedEventArgs args) {
            ViewModel?.RefreshPhonemizers();
            PhonemizersMenu.Open((Control)sender);
            args.Handled = true;
        }

        void PhonemizerButtonContextRequested(object sender, ContextRequestedEventArgs args) {
            args.Handled = true;
        }

        void RendererButtonClicked(object sender, RoutedEventArgs args) {
            ViewModel?.RefreshRenderers();
            if (ViewModel?.RenderersMenuItems?.Count > 0) {
                RenderersMenu.Open((Control)sender);
            }
            args.Handled = true;
        }

        void RendererButtonContextRequested(object sender, ContextRequestedEventArgs args) {
            args.Handled = true;
        }

        void VolumeFaderPointerPressed(object sender, PointerPressedEventArgs args) {
            if (args.GetCurrentPoint((Visual?)sender).Properties.IsRightButtonPressed && ViewModel != null) {
                ViewModel.Volume = 0;
                args.Handled = true;
            }
        }

        void PanFaderPointerPressed(object sender, PointerPressedEventArgs args) {
            if (args.GetCurrentPoint((Visual?)sender).Properties.IsRightButtonPressed && ViewModel != null) {
                ViewModel.Pan = 0;
                args.Handled = true;
            }
        }

        void VolumeFaderContextRequested(object sender, ContextRequestedEventArgs args) {
            if (ViewModel != null) {
                ViewModel.Volume = 0;
            }
            args.Handled = true;
        }

        void PanFaderContextRequested(object sender, ContextRequestedEventArgs args) {
            if (ViewModel != null) {
                ViewModel.Pan = 0;
            }
            args.Handled = true;
        }

        async void TrackSettingsButtonClicked(object sender, RoutedEventArgs args) {
            if (track?.Singer != null && track.Singer.Found) {
                if (TopLevel.GetTopLevel(this) is Window window) {
                    var dialog = new Views.TrackSettingsDialog(track);
                    await dialog.ShowDialog(window);
                }
            }
        }

        void VolumePointerPressed(object sender, PointerPressedEventArgs args) {
            if (args.ClickCount == 2 && ViewModel != null) {
                ActivateVolumeOrPanTextBox(
                    VolumeTextBox, string.Format("{0:0.0}", ViewModel.Volume));
                args.Handled = true;
                args.Pointer.Capture(sender as IInputElement);
            }
        }
        void PanPointerPressed(object sender, PointerPressedEventArgs args) {
            if (args.ClickCount == 2 && ViewModel != null) {
                ActivateVolumeOrPanTextBox(
                    PanTextBox, string.Format("{0:0}", ViewModel.Pan));
                args.Handled = true;
            }
        }
        void VolumeOrPanTextBoxKeyDown(object sender, KeyEventArgs args) {
            if (args.Key == Key.Enter) {
                FinishVolumeOrPanInput(sender, true);
                args.Handled = true;
            } else if (args.Key == Key.Escape) {
                FinishVolumeOrPanInput(sender, false);
                args.Handled = true;
            }
        }
        void VolumeOrPanTextBoxLostFocus(object sender, FocusChangedEventArgs args) {
            FinishVolumeOrPanInput(sender, true);
            args.Handled = true;
        }
        void VolumeOrPanSliderValueChanged(object sender, RangeBaseValueChangedEventArgs args) {
            VolumeTextBox.IsVisible = false;
            VolumeTextBox.IsEnabled = false;
            PanTextBox.IsVisible = false;
            PanTextBox.IsEnabled = false;
        }

        void ActivateVolumeOrPanTextBox(TextBox textBox, string text) {
            textBox.Text = text;
            textBox.IsEnabled = true;
            textBox.IsVisible = true;
            textBox.Focus();
        }

        private void MoveHandleScrolled(object? sender, PointerWheelEventArgs e) {
            Dispatcher.UIThread.Post(() => MoveHandleMoved(sender, e));
        }

        private void MoveHandlePressed(object? sender, PointerPressedEventArgs e) {
            var control = sender as Control;
            var pointer = e.GetCurrentPoint(control);
            if (control != null && pointer.Properties.IsLeftButtonPressed) {
                e.Pointer.Capture(control);
            }
        }

        private void MoveHandleReleased(object? sender, PointerReleasedEventArgs e) {
            if (e.InitialPressMouseButton == MouseButton.Left && canvas != null) {
                Cursor = null;
                Point point = e.GetPosition(canvas);
                int tracksMaxIdx = DocManager.Inst.Project.tracks.Count - 1;
                double sizeHidden = Math.Abs(Offset.Y);
                if (track != null) {
                    bool isAboveCenter = Canvas.GetTop(this) + TrackHeight / 2 > point.Y;
                    int position = (int) Math.Round((sizeHidden + point.Y - (isAboveCenter ? 0 : TrackHeight)) / TrackHeight);
                    int idx = Math.Clamp(position, 0, tracksMaxIdx);

                    if (track.TrackNo != idx) {
                        DocManager.Inst.StartUndoGroup("command.track.order");
                        DocManager.Inst.ExecuteCmd(new SetTrackNoCommand(DocManager.Inst.Project, track, idx));
                        DocManager.Inst.EndUndoGroup();
                    }

                }
                if (canvas.TrackMover != null) {
                    canvas.TrackMover.IsVisible = false;
                }
            }
            e.Pointer.Capture(null);
        }

        private void MoveHandleMoved(object? sender, PointerEventArgs e) {
            var control = sender as Control;
            var pointer = e.GetCurrentPoint(control);
            if (pointer.Properties.IsLeftButtonPressed && e.Pointer.Captured == control && canvas?.TrackMover != null) {
                canvas.TrackMover.IsVisible = true;
                Cursor = ViewConstants.cursorSizeNS;
                double maxHeight = DocManager.Inst.Project.tracks.Count * TrackHeight;
                Point point = e.GetPosition(canvas);
                double offset = Math.Abs(Offset.Y);
                double leftOver = offset % TrackHeight;
                if (track != null) {
                    double position = Math.Round((point.Y + leftOver) / TrackHeight) * TrackHeight;
                    double finalPos = Math.Clamp(position - leftOver, -leftOver, maxHeight - offset);
                    Canvas.SetTop(canvas.TrackMover, finalPos);
                }
            }
        }

        private void FinishVolumeOrPanInput(object sender, bool commit) {
            if (sender == VolumeTextBox) {
                if (!VolumeTextBox.IsVisible) {
                    return; // Avoid double commit.
                }
                VolumeTextBox.IsVisible = false;
                VolumeTextBox.IsEnabled = false;
                if (commit && double.TryParse(VolumeTextBox.Text, out var volume) && ViewModel != null) {
                    ViewModel.Volume = Math.Clamp(volume, VolumeSlider.Minimum, VolumeSlider.Maximum);
                }
            } else {
                if (!PanTextBox.IsVisible) {
                    return; // Avoid double commit.
                }
                PanTextBox.IsVisible = false;
                PanTextBox.IsEnabled = false;
                if (commit && double.TryParse(PanTextBox.Text, out var pan) && ViewModel != null) {
                    ViewModel.Pan = Math.Clamp(pan, PanSlider.Minimum, PanSlider.Maximum);
                }
            }
        }

        public void Dispose() {
            unbinds.ForEach(u => u.Dispose());
            unbinds.Clear();
        }
    }
}

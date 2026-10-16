using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.App.ViewModels;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace OpenUtau.App.Controls {
    /// <summary>
    /// The right hand inspector column, where Synthesizer V Studio 2 keeps its Voice and Parameters
    /// panels. It shows the selected track's properties by hosting that track's full (non compact)
    /// <see cref="TrackHeader"/>. List row and inspector therefore share one view model per track,
    /// so both stay in sync without extra plumbing.
    /// </summary>
    public partial class TrackInspector : UserControl {
        internal static readonly StyledProperty<TrackHeaderCanvas?> CanvasProperty =
            AvaloniaProperty.Register<TrackInspector, TrackHeaderCanvas?>(nameof(Canvas));

        /// <summary>The track list, which owns the per track view models.</summary>
        internal TrackHeaderCanvas? Canvas {
            get => GetValue(CanvasProperty);
            set => SetValue(CanvasProperty, value);
        }

        private readonly List<IDisposable> subscriptions = new List<IDisposable>();
        private UTrack? track;

        public TrackInspector() {
            InitializeComponent();
            Header.ShowAvatar = false;
            // The inspector sizes the header to its contents instead of to a track lane.
            Header.Width = double.NaN;
            Header.Height = double.NaN;
            // Typed placeholder, so the header's compiled bindings never see the window's view model
            // (the window's data context is inherited before a track is picked).
            Header.DataContext = new TrackHeaderViewModel();
            subscriptions.Add(MessageBus.Current.Listen<TrackSelectionEvent>()
                .Subscribe(e => Show(TrackToShow(e.selectedTracks))));
            // The track list builds the header (and its view model) when a track appears and refreshes
            // the view model on every change, so this only has to (re)connect the two.
            subscriptions.Add(MessageBus.Current.Listen<TracksRefreshEvent>()
                .Subscribe(_ => Show(track)));
        }

        /// <summary>
        /// Like Synthesizer V, the panel always shows a track: the last selected one, or the first
        /// track of the project when the selection is empty.
        /// </summary>
        static UTrack? TrackToShow(UTrack[] selectedTracks) => selectedTracks.Length > 0
            ? selectedTracks[^1]
            : DocManager.Inst.Project?.tracks.FirstOrDefault();

        void Show(UTrack? target) {
            if (target == null) {
                track = null;
                Header.IsVisible = false;
                Header.ViewModel = null;
                Header.DataContext = null;
                return;
            }
            track = target;
            var viewModel = Canvas?.GetHeaderViewModel(target);
            Header.IsVisible = viewModel != null;
            Header.ViewModel = viewModel;
            Header.DataContext = viewModel;
            // Also hands the header the track itself, for the track settings dialog.
            Header.Bind(target);
            Header.UpdateVisibility();
        }
    }
}

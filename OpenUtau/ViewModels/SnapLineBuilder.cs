using System;
using System.Collections.Generic;

using OpenUtau.Core.Ustx;

namespace OpenUtau.App.ViewModels {
    /// <summary>
    /// The bar/beat traversal that decides where the timeline draws its lines.
    /// It used to run inside <c>TickBackground.Render</c>, which meant the tick list snapping is
    /// based on only existed after the background painted once, and every frame cleared and refilled
    /// a shared collection. The view models compute it now (<see cref="NotesViewModel.UpdateSnapLines"/>
    /// and <see cref="TracksViewModel.UpdateSnapLines"/>) and the control draws the lines from the
    /// resulting snapshot.
    /// </summary>
    internal static class SnapLineBuilder {
        /// <summary>
        /// Tick positions of the lines drawn between the left edge of the viewport and its right
        /// edge, ascending, plus the first bar line past the right edge: snapping uses that last one
        /// to bracket ticks near the edge, and it is the one line the renderer never drew.
        /// Returns null when the inputs cannot produce a traversal (nothing is laid out or zoomed
        /// yet), in which case the caller keeps its previous snapshot, the way the renderer kept
        /// whatever the collection held when it returned early.
        /// </summary>
        internal static int[]? Build(
            UProject project, double tickWidth, double tickOffset, int tickOrigin,
            double viewportWidth, int snapDiv) {
            if (tickWidth <= 0 || snapDiv <= 0 || project.resolution <= 0) {
                return null;
            }
            var snapLines = new List<int>();
            int snapUnit = project.resolution * 4 / snapDiv;
            while (snapUnit * tickWidth < ViewConstants.MinTicklineWidth) {
                snapUnit *= 2; // Avoid drawing too dense.
            }
            double minLineTick = ViewConstants.MinTicklineWidth / tickWidth;
            double leftTick = tickOffset + tickOrigin;
            double rightTick = tickOffset + tickOrigin + viewportWidth / tickWidth;

            project.timeAxis.TickPosToBarBeat(tickOrigin, out int bar, out int beat, out int remainingTicks);
            if (bar > 0) {
                bar--;
            }
            int barTick = project.timeAxis.BarBeatToTickPos(bar, 0);
            while (barTick <= rightTick) {
                snapLines.Add(barTick);
                var timeSig = project.timeAxis.TimeSignatureAtBar(bar);
                int nextBarTick = project.timeAxis.BarBeatToTickPos(bar + 1, 0);
                int ticksPerBeat = project.resolution * 4 * timeSig.beatPerBar / timeSig.beatUnit;
                int ticksPerLine = snapUnit;
                if (ticksPerBeat < snapUnit) {
                    ticksPerLine = ticksPerBeat;
                } else if (ticksPerBeat % snapUnit != 0) {
                    if (ticksPerBeat > minLineTick) {
                        ticksPerLine = ticksPerBeat;
                    } else {
                        ticksPerLine = nextBarTick - barTick;
                    }
                }
                if (nextBarTick > leftTick) {
                    for (int tick = barTick + ticksPerLine; tick < nextBarTick; tick += ticksPerLine) {
                        snapLines.Add(tick);
                    }
                }
                barTick = nextBarTick;
                bar++;
            }
            snapLines.Add(barTick);
            return snapLines.ToArray();
        }
    }
}

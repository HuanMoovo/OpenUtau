using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core {
    public class TimeAxis {
        class TimeSigSegment {
            public int barPos;
            public int barEnd = int.MaxValue;
            public int tickPos;
            public int tickEnd = int.MaxValue;

            public int beatPerBar;
            public int beatUnit;

            public int ticksPerBar;
            public int ticksPerBeat;
        }

        class TempoSegment {
            public int tickPos;
            public int tickEnd = int.MaxValue;

            public double bpm;
            public int beatPerBar;
            public int beatUnit;

            public double msPos;
            public double msEnd = double.MaxValue;
            public double msPerTick;
            public double ticksPerMs;

            public int Ticks => tickEnd - tickPos;
        }

        const double DefaultBpm = 120.0;
        const int DefaultBeatPerBar = 4;
        const int DefaultBeatUnit = 4;

        readonly List<TimeSigSegment> timeSigSegments = new List<TimeSigSegment>();
        readonly List<TempoSegment> tempoSegments = new List<TempoSegment>();

        public long Timestamp { get; private set; }

        /// <summary>
        /// A bpm is only usable if the tick&lt;-&gt;ms conversion it defines is finite and
        /// positive. Zero, NaN or infinity fail outright, and a subnormal one (below about
        /// 6.9e-307 at a resolution of 480) overflows msPerTick to infinity while still
        /// reading as "finite and positive" itself: msPerTick * 0 is NaN, so even tick 0
        /// then converts to NaN — a playback start that <c>TimeSpan.FromMilliseconds</c>
        /// rejects, which surfaces to the user as "Failed to render".
        /// </summary>
        static bool IsUsableBpm(double bpm, int resolution) {
            if (!double.IsFinite(bpm) || bpm <= 0) {
                return false;
            }
            double msPerTick = 60.0 * 1000.0 / (bpm * resolution);
            return double.IsFinite(msPerTick) && msPerTick > 0;
        }

        public void BuildSegments(UProject project) {
            Timestamp = DateTime.Now.ToFileTimeUtc();
            timeSigSegments.Clear();
            for (var i = 0; i < project.timeSignatures.Count; ++i) {
                var timesig = project.timeSignatures[i];
                var posTick = 0;
                if (i > 0) {
                    var lastBarPos = project.timeSignatures[i - 1].barPosition;
                    posTick = timeSigSegments.Last().tickPos
                        + timeSigSegments.Last().ticksPerBar * (timesig.barPosition - lastBarPos);
                } else {
                    if(timesig.barPosition != 0) {
                        throw new Exception("First time signature must be at bar 0.");
                    }
                }
                // A non-positive beat unit divides by zero here, and a non-positive
                // beat per bar makes ticksPerBar zero, which divides by zero later.
                var beatPerBar = timesig.beatPerBar > 0 ? timesig.beatPerBar : DefaultBeatPerBar;
                var beatUnit = timesig.beatUnit > 0 ? timesig.beatUnit : DefaultBeatUnit;
                timeSigSegments.Add(new TimeSigSegment {
                    barPos = timesig.barPosition,
                    tickPos = posTick,
                    beatPerBar = beatPerBar,
                    beatUnit = beatUnit,
                    ticksPerBar = project.resolution * 4 * beatPerBar / beatUnit,
                    ticksPerBeat = project.resolution * 4 / beatUnit,
                });
            }
            if (timeSigSegments.Count == 0) {
                // Without this a project with no time signature leaves every bar/beat
                // lookup with nothing to return, and leaves tempoSegments empty too.
                timeSigSegments.Add(new TimeSigSegment {
                    barPos = 0,
                    tickPos = 0,
                    beatPerBar = DefaultBeatPerBar,
                    beatUnit = DefaultBeatUnit,
                    ticksPerBar = project.resolution * 4 * DefaultBeatPerBar / DefaultBeatUnit,
                    ticksPerBeat = project.resolution * 4 / DefaultBeatUnit,
                });
            }
            for (var i = 0; i < timeSigSegments.Count - 1; ++i) {
                timeSigSegments[i].barEnd = timeSigSegments[i + 1].barPos;
                timeSigSegments[i].tickEnd = timeSigSegments[i + 1].tickPos;
            }

            tempoSegments.Clear();
            tempoSegments.AddRange(timeSigSegments.Select(sigseg => new TempoSegment {
                tickPos = sigseg.tickPos,
                beatPerBar = sigseg.beatPerBar,
                beatUnit = sigseg.beatUnit,
            }));
            for (var i = 0; i < project.tempos.Count; ++i) {
                var tempo = project.tempos[i];
                if (i == 0) {
                    if(tempo.position != 0) {
                        throw new Exception("First tempo must be at tick 0.");
                    }
                }
                var index = tempoSegments.FindIndex(seg => seg.tickPos >= tempo.position);
                if (index < 0) {
                    tempoSegments.Add(new TempoSegment {
                        tickPos = tempo.position,
                        bpm = tempo.bpm,
                        beatPerBar = tempoSegments.Last().beatPerBar,
                        beatUnit = tempoSegments.Last().beatUnit,
                    });
                } else if (tempoSegments[index].tickPos == tempo.position) {
                    tempoSegments[index].bpm = tempo.bpm;
                } else {
                    tempoSegments.Insert(index, new TempoSegment {
                        tickPos = tempo.position,
                        bpm = tempo.bpm,
                        beatPerBar = tempoSegments[index - 1].beatPerBar,
                        beatUnit = tempoSegments[index - 1].beatUnit,
                    });
                }
            }
            if (!IsUsableBpm(tempoSegments[0].bpm, project.resolution)) {
                // No tempo at tick 0, or one that cannot be divided by. Everything
                // after inherits from here, so this segment must be valid.
                tempoSegments[0].bpm = DefaultBpm;
            }
            for (var i = 0; i < tempoSegments.Count - 1; ++i) {
                if (!IsUsableBpm(tempoSegments[i + 1].bpm, project.resolution)) {
                    tempoSegments[i + 1].bpm = tempoSegments[i].bpm;
                }
                tempoSegments[i].tickEnd = tempoSegments[i + 1].tickPos;
            }
            for (var i = 0; i < tempoSegments.Count; ++i) {
                tempoSegments[i].msPerTick = 60.0 * 1000.0 / (tempoSegments[i].bpm * project.resolution);
                tempoSegments[i].ticksPerMs = tempoSegments[i].bpm * project.resolution / (60.0 * 1000.0);
                if (i > 0) {
                    tempoSegments[i].msPos = tempoSegments[i - 1].msPos + tempoSegments[i - 1].Ticks * tempoSegments[i - 1].msPerTick;
                    tempoSegments[i - 1].msEnd = tempoSegments[i].msPos;
                }
            }
            // Start keys for the lookups' binary search, mirroring the segments
            // the way tickEnd mirrors the next tickPos.
            tempoTickStarts = new double[tempoSegments.Count];
            tempoMsStarts = new double[tempoSegments.Count];
            for (var i = 0; i < tempoSegments.Count; ++i) {
                tempoTickStarts[i] = tempoSegments[i].tickPos;
                tempoMsStarts[i] = tempoSegments[i].msPos;
            }
            timeSigTickStarts = new double[timeSigSegments.Count];
            timeSigBarStarts = new double[timeSigSegments.Count];
            for (var i = 0; i < timeSigSegments.Count; ++i) {
                timeSigTickStarts[i] = timeSigSegments[i].tickPos;
                timeSigBarStarts[i] = timeSigSegments[i].barPos;
            }
        }

        /// <summary>
        /// Key array length below which <see cref="SegmentIndexAt"/> walks the array
        /// and above which it binary-searches; both return the same index. Set by
        /// measurement: the walk's early exit wins on the short lists most projects
        /// have, the search wins once the list is long (2.5x at 256 tempo segments,
        /// 25x at 4096, on the lookups' benchmark).
        /// </summary>
        const int LinearScanLimit = 64;

        // Start positions of the segments, one array per lookup key, built by
        // BuildSegments and shared by Clone like the segment lists themselves.
        // The search below reads these instead of the segment objects, which keeps
        // its steps free of dispatch and the lookup methods small enough to inline.
        double[] tempoTickStarts = Array.Empty<double>();
        double[] tempoMsStarts = Array.Empty<double>();
        double[] timeSigTickStarts = Array.Empty<double>();
        double[] timeSigBarStarts = Array.Empty<double>();

        /// <summary>
        /// Index of the segment containing <paramref name="target"/>: the last
        /// segment whose start is at or before it. This is the same index the former
        /// linear scans returned: the segments are sorted by start and each ends
        /// where the next begins, so the first segment whose end passes the target
        /// is the last one that starts before it. A project with many tempo or time
        /// signature changes would otherwise pay an O(n) walk per tick conversion,
        /// on the render thread, so long lists are binary-searched.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int SegmentIndexAt(double[] starts, double target) {
            if (starts.Length <= LinearScanLimit) {
                for (var i = 0; i < starts.Length; ++i) {
                    if (starts[i] >= target) {
                        return starts[i] == target ? i : Math.Max(0, i - 1);
                    }
                }
                return starts.Length - 1;
            }
            int lo = 0, hi = starts.Length;
            while (lo < hi) {
                var mid = (lo + hi) >> 1;
                if (starts[mid] < target) {
                    lo = mid + 1;
                } else {
                    hi = mid;
                }
            }
            if (lo < starts.Length && starts[lo] == target) {
                return lo;
            }
            return Math.Max(0, lo - 1);
        }

        /// <summary>
        /// Finds the tempo segment containing a tick. Never throws: a tick past the
        /// last segment, or a NaN one, is clamped instead. These lookups run on the
        /// render thread, where an exception takes the whole app down.
        /// </summary>
        TempoSegment TempoSegmentAtTick(double tick) {
            if (!double.IsNaN(tick)) {
                return tempoSegments[SegmentIndexAt(tempoTickStarts, tick)];
            }
            return tempoSegments[tempoSegments.Count - 1];
        }

        /// <summary>
        /// Ms counterpart of <see cref="TempoSegmentAtTick"/>. Also never throws.
        /// </summary>
        TempoSegment TempoSegmentAtMsPos(double ms) {
            if (!double.IsNaN(ms)) {
                return tempoSegments[SegmentIndexAt(tempoMsStarts, ms)];
            }
            return tempoSegments[tempoSegments.Count - 1];
        }

        TimeSigSegment TimeSigSegmentAtTick(int tick) {
            return timeSigSegments[SegmentIndexAt(timeSigTickStarts, tick)];
        }

        TimeSigSegment TimeSigSegmentAtBar(int bar) {
            return timeSigSegments[SegmentIndexAt(timeSigBarStarts, bar)];
        }

        public double GetBpmAtTick(int tick) {
            var segment = TempoSegmentAtTick(tick);
            return segment.bpm;
        }

        public double TickPosToMsPos(double tick) {
            var segment = TempoSegmentAtTick(tick);
            return segment.msPos + segment.msPerTick * (tick - segment.tickPos);
        }

        public double MsPosToNonExactTickPos(double ms) {
            var segment = TempoSegmentAtMsPos(ms);
            double tickPos = segment.tickPos + (ms - segment.msPos) * segment.ticksPerMs;
            return tickPos;
        }

        public int MsPosToTickPos(double ms) {
            var segment = TempoSegmentAtMsPos(ms);
            double tickPos = segment.tickPos + (ms - segment.msPos) * segment.ticksPerMs;
            if (!double.IsFinite(tickPos)) {
                // Casting NaN or infinity saturates silently, which hides the bad
                // value. Fall back to the segment start instead.
                return segment.tickPos;
            }
            return (int)Math.Round(Math.Clamp(tickPos, int.MinValue, int.MaxValue));
        }

        public int TicksBetweenMsPos(double msPos, double msEnd) {
            return MsPosToTickPos(msEnd) - MsPosToTickPos(msPos);
        }

        public double MsBetweenTickPos(double tickPos, double tickEnd) {
            return TickPosToMsPos(tickEnd) - TickPosToMsPos(tickPos);
        }

        /// <summary>
        /// Convert ms duration to tick at a given reference tick position
        /// </summary>
        /// <param name="durationMs">Duration in ms, positive value means starting from refTickPos, negative value means ending at refTickPos</param>
        /// <param name="refTickPos">Reference tick position</param>
        /// <returns>Duration in ticks</returns>
        public int MsToTickAt(double offsetMs, int refTickPos) {
            return TicksBetweenMsPos(
                TickPosToMsPos(refTickPos), 
                TickPosToMsPos(refTickPos) + offsetMs);
        }

        public void TickPosToBarBeat(int tick, out int bar, out int beat, out int remainingTicks) {
            var segment = TimeSigSegmentAtTick(tick);
            bar = segment.barPos + (tick - segment.tickPos) / segment.ticksPerBar;
            int tickInBar = tick - segment.tickPos - segment.ticksPerBar * (bar - segment.barPos);
            beat = tickInBar / segment.ticksPerBeat;
            remainingTicks = tickInBar - beat * segment.ticksPerBeat;
        }

        public int BarBeatToTickPos(int bar, int beat) {
            var segment = TimeSigSegmentAtBar(bar);
            return segment.tickPos + segment.ticksPerBar * (bar - segment.barPos) + segment.ticksPerBeat * beat;
        }

        public void NextBarBeat(int bar, int beat, out int nextBar, out int nextBeat) {
            nextBar = bar;
            nextBeat = beat + 1;
            var segment = TimeSigSegmentAtBar(bar);
            if (nextBeat >= segment.beatPerBar) {
                nextBar++;
                nextBeat = 0;
            }
        }

        public UTempo[] TemposBetweenTicks(int start, int end) {
            // A loop rather than LINQ: phrase snapshots call this twice for every phoneme.
            // Segments are sorted, so it starts at the one containing `start` and stops
            // at the first that begins at or after `end`, instead of walking the whole list.
            var list = new List<UTempo>(1);
            var count = tempoSegments.Count;
            var first = count <= LinearScanLimit ? 0 : SegmentIndexAt(tempoTickStarts, start);
            for (var i = first; i < count; ++i) {
                var tempo = tempoSegments[i];
                if (tempo.tickPos >= end) {
                    break;
                }
                if (start < tempo.tickEnd) {
                    list.Add(new UTempo { position = tempo.tickPos, bpm = tempo.bpm });
                }
            }
            if (list.Count == 0) {
                var segment = TempoSegmentAtTick(start);
                list.Add(new UTempo { position = start, bpm = segment.bpm });
            }
            return list.ToArray();
        }

        public UTimeSignature TimeSignatureAtTick(int tick) {
            var segment = TimeSigSegmentAtTick(tick);
            return new UTimeSignature {
                barPosition = segment.barPos,
                beatPerBar = segment.beatPerBar,
                beatUnit = segment.beatUnit,
            };
        }

        public UTimeSignature TimeSignatureAtBar(int bar) {
            var segment = TimeSigSegmentAtBar(bar);
            return new UTimeSignature {
                barPosition = segment.barPos,
                beatPerBar = segment.beatPerBar,
                beatUnit = segment.beatUnit,
            };
        }

        public TimeAxis Clone() {
            var clone = new TimeAxis();
            // Shallow copy segments since they are unmodified after built, and
            // share the lookup key arrays, which are replaced rather than mutated.
            clone.timeSigSegments.AddRange(timeSigSegments);
            clone.tempoSegments.AddRange(tempoSegments);
            clone.tempoTickStarts = tempoTickStarts;
            clone.tempoMsStarts = tempoMsStarts;
            clone.timeSigTickStarts = timeSigTickStarts;
            clone.timeSigBarStarts = timeSigBarStarts;
            return clone;
        }
    }
}

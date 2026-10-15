using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

// Micro-benchmark for TimeAxis segment lookups.
// Usage: dotnet run -c Release -- [--quick]
// Prints TSV: segments  op  ns_per_op  checksum
// The checksum lets us verify that a refactor preserved results exactly.
internal static class Program {
    const int Queries = 400_000;
    const int Reps = 5;

    static void Main(string[] args) {
        bool quick = args.Length > 0 && args[0] == "--quick";
        int[] segmentCounts = quick
            ? new[] { 1, 16, 256 }
            : new[] { 1, 4, 16, 64, 256, 1024, 4096 };

        Console.WriteLine("segments\top\tns_per_op\tchecksum");
        foreach (int n in segmentCounts) {
            var project = MakeProject(n);
            var axis = new TimeAxis();
            axis.BuildSegments(project);

            int totalTicks = n * TicksPerBar;
            var ticks = new int[Queries];
            var msVals = new double[Queries];
            var bars = new int[Queries];
            var rnd = new Random(12345);
            double maxMs = axis.TickPosToMsPos(totalTicks - 1);
            for (int i = 0; i < Queries; i++) {
                ticks[i] = rnd.Next(0, totalTicks);
                msVals[i] = rnd.NextDouble() * maxMs;
                bars[i] = rnd.Next(0, n);
            }

            Bench(n, "TickPosToMsPos", () => {
                double s = 0;
                for (int i = 0; i < Queries; i++) s += axis.TickPosToMsPos(ticks[i]);
                return s;
            });
            Bench(n, "MsPosToTickPos", () => {
                long s = 0;
                for (int i = 0; i < Queries; i++) s += axis.MsPosToTickPos(msVals[i]);
                return s;
            });
            Bench(n, "GetBpmAtTick", () => {
                double s = 0;
                for (int i = 0; i < Queries; i++) s += axis.GetBpmAtTick(ticks[i]);
                return s;
            });
            Bench(n, "TickPosToBarBeat", () => {
                long s = 0;
                for (int i = 0; i < Queries; i++) {
                    axis.TickPosToBarBeat(ticks[i], out int bar, out int beat, out int rem);
                    s += bar + beat + rem;
                }
                return s;
            });
            Bench(n, "BarBeatToTickPos", () => {
                long s = 0;
                for (int i = 0; i < Queries; i++) s += axis.BarBeatToTickPos(bars[i], 2);
                return s;
            });
            Bench(n, "TemposBetweenTicks", () => {
                long s = 0;
                for (int i = 0; i < Queries; i++) {
                    var tempos = axis.TemposBetweenTicks(ticks[i], ticks[i] + TicksPerBar);
                    s += tempos.Length;
                }
                return s;
            });
        }
    }

    const int TicksPerBar = 1920; // resolution 480 * 4 beats

    static UProject MakeProject(int n) {
        var project = new UProject {
            tempos = new List<UTempo>(n),
            timeSignatures = new List<UTimeSignature>(n / 4 + 1),
        };
        for (int i = 0; i < n; i++) {
            project.tempos.Add(new UTempo(i * TicksPerBar, 120.0 + (i % 60)));
        }
        for (int i = 0; i < n; i += 4) {
            project.timeSignatures.Add(new UTimeSignature(i, 4, 4));
        }
        return project;
    }

    static void Bench(int segments, string name, Func<double> body) {
        body(); // warm up JIT
        body();
        double best = double.MaxValue;
        double checksum = 0;
        for (int r = 0; r < Reps; r++) {
            var sw = Stopwatch.StartNew();
            checksum = body();
            sw.Stop();
            double nsPerOp = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / Queries;
            if (nsPerOp < best) best = nsPerOp;
        }
        Console.WriteLine($"{segments}\t{name}\t{best:F1}\t{checksum:F3}");
    }
}

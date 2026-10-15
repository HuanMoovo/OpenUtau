# TimeAxis benchmark

Micro-benchmark for the `TimeAxis` segment lookups (`TickPosToMsPos`, `MsPosToTickPos`,
`GetBpmAtTick`, `TickPosToBarBeat`, `BarBeatToTickPos`, `TemposBetweenTicks`).

    dotnet run -c Release --project bench/TimeAxisBench            # full run
    dotnet run -c Release --project bench/TimeAxisBench -- --quick # 3 sizes

It prints TSV: `segments  op  ns_per_op  checksum`. The checksum sums every result over
400k random queries, so two implementations that print the same checksums returned the
same values for all of them.

## Reading the numbers

- Per-op nanoseconds, best of 5 reps of 400k calls, after two warmup passes.
- Set `DOTNET_TieredCompilation=0` for reproducible runs. With tiering on (the default)
  JIT tier promotion can land inside a measurement; compare builds back to back on an
  idle machine and treat differences under ~10% as noise.
- The lookups walk the list for `TimeAxis.LinearScanLimit` (64) segments or fewer and
  binary-search the key arrays above that, so the large segment counts are the
  interesting rows.

To compare against an older commit, check it out in a second worktree and run the same
command there.

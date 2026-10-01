# Benchmark report format (version 5)

`comparison.json` contains `SchemaVersion`, `RecordingId`, `HighestPassingSpeed` (nullable), and an array of `Speeds`. Each speed has five `Runs`, a `Passed` verdict, a failure reason, and `MeasurementWallOverheadPercent`. Every run is also saved as `run-N-summary.json`; both use the same version 5 JSON shape and PascalCase property names. The shared coordinator emits these field names and units for both C# and Rust. `ReplayStateSource` is `synthetic` when no fixture is supplied and `fixture` otherwise. `FixtureSha256` is null for synthetic runs. Synthetic replay uses invented account, filter, and starting book state; its quote values are not historical results. C# selects each symbol's price tick and quantity step as 10 raised to the SBE exponents in its first quote or depth frame, then seeds its book from market observations. Compare C# and Rust decision hashes only when both use equivalent starting state and quote units.

## Latency and throughput

Every `LatencySummary` is in **nanoseconds** and contains `Count`, `P50`, `P90`, `P95`, `P99`, `P999`, and `Maximum`. It uses nearest-rank percentiles. `SendToDecisionNanoseconds` is measured from the simulator's actual send start to the last decision produced for a frame. `SendToProcessingNanoseconds` ends after the whole frame handler; it includes frames with no decision. `ReceiveToDecisionNanoseconds` starts when the complete WebSocket frame is available in the app. `ProcessingNanoseconds` ends after the frame handler. `Application` includes `DecodeNanoseconds`, `BookNanoseconds`, `SignalNanoseconds`, and `QuoteNanoseconds` with the same percentile fields. A trade frame with multiple trades contributes one frame sample and a count of all its decisions.

`PerSecond` contains client receive windows with `Second` (zero based), `Frames`, `Bytes`, and `Decisions`. `AchievedFramesPerSecond` and `AchievedDecisionsPerSecond` divide measured totals by measured client receive duration. `SentFrames`, `SentBytes`, `ScheduledDurationNanoseconds`, `ActualSendDurationNanoseconds`, and `ReplaySendLatenessNanoseconds` describe the source. The capacity verdict uses P99 send-to-decision and P99 send-to-processing, complete delivery, replay send lateness, and the minimum measured window. All five runs at a speed must pass.

## Resource measures

`Application.CpuNanoseconds`, `CpuCores` (CPU time divided by elapsed time), and `CpuNanosecondsPerFrame` cover the measured window. `ResidentBytes`, `PeakResidentBytes`, and `ManagedHeapBytes` are separate memory measures. `ManagedAllocatedBytes` and `ManagedAllocatedBytesPerFrame` count C# managed allocations, not native allocations. `GcCollections` is `[Gen0, Gen1, Gen2]`; `GcPauseNanoseconds` is cumulative stop-the-world time during the measured window. `IndividualGcPauseNanoseconds` and `FramesOverlappingGc` are populated in diagnostic runs, which cannot produce a capacity verdict.

`HardwareCounters` contains nullable `Cycles`, `Instructions`, `CacheMisses`, `BranchMisses`, and `ContextSwitches`, with `Scope: "whole_process"`. Those values include startup and warm-up. `UnavailableReason` names counters that could not be collected. Rust allocator bytes must be labelled as allocator allocations rather than C# managed allocations; process memory and CPU fields remain directly comparable.

## Per-frame samples

`run-N-samples.csv` has the columns below. Timestamp and stage columns ending in `_ticks` are **monotonic clock ticks**, not Unix time; divide by `ClockFrequency` and multiply by 1,000,000,000 for nanoseconds. C# and Rust processes must use the same host monotonic clock for source-to-app subtraction. Rust may use `CLOCK_MONOTONIC` nanoseconds and set `ClockFrequency` to 1,000,000,000. A zero `last_decision_ticks` means no decision; zero stage ticks mean that stage did not run. `index` is the one-based ordinal in the original recording, including text messages.

```text
index,type,bytes,template,send_ticks,receive_ticks,end_ticks,last_decision_ticks,decisions,decode_ticks,book_ticks,signal_ticks,quote_ticks,gc_overlap
```

`type` is the WebSocket message type (`0` text, `1` binary). `template` is the SBE template ID or zero when none was decoded. `gc_overlap` marks a GC suspension overlapping client frame processing in a diagnostic run. `run-N-gc-pauses.json` lists individual pause start/end UTC ticks and suspension reason for the measured window; those timestamps are only for diagnostic correlation.

## Decision equivalence

`DecisionHash` covers every decision in recording order, including warm-up. Start with 64-bit FNV offset `14695981039346656037`. For each decision, mix these signed integer values in order: instrument ID, bid price ticks, bid quantity, bid action, ask price ticks, ask quantity, ask action. Each mix is `hash = (hash XOR value_as_unsigned_64_bits) * 1099511628211`, wrapping at 64 bits. Actions are `None=0`, `Place=1`, `Keep=2`, `Replace=3`, `Cancel=4`. Render the result as 16 lowercase hexadecimal digits. A fixture-free run checks decision repeatability across C# runs. The Rust application uses matching synthetic initialization or the same shared fixture. Both hashes must match before performance ratios are published.


## Version 5 additions and compatibility

The summary schema is 5; the application-to-coordinator frame report remains
schema 3 with additive optional properties. Existing per-frame CSV columns and
nanosecond percentile definitions are unchanged. Scripts and launch procedures
are documented in [testing.md](testing.md).

Each run adds `RunKind` (`smoke`, `diagnostic`, or `measurement`), nullable
`CapacityPassed`, `FinalStateHash`, `DatasetSha256`, nullable
`RequestedMaxMessages`, `P99LimitNanoseconds`, `ReplayLatenessLimitNanoseconds`,
`Build`, and `Host`. `SentFrames` is the actual selected count. Unlimited selection
sets `RequestedMaxMessages` to null; a larger limit than the source selects all
available messages. `Passed` is a functional verdict for smoke runs; only
`CapacityPassed` represents a measured capacity verdict. `HighestPassingSpeed`
is always null for smoke/diagnostic runs.

Dataset SHA-256 consumes each selected message in order: signed little-endian
int64 sequence, signed little-endian int64 elapsed nanoseconds, one byte message
type, signed little-endian int64 payload length, and exact payload bytes. The
recording UUID is recorded separately. Original offsets, including initial silence,
are retained. SQL prefix selection does not change the recording.

`Application.AllocatorAllocatedBytes` and `AllocatorAllocatedBytesPerFrame` count
requested Rust allocator bytes, including reallocations, across the process in
the measured window. `Application.AllocationScope` labels the counter. These
allocator fields are null for C#. Managed heap/allocation and GC fields are null
for Rust; empty GC diagnostic arrays mean no GC instrumentation. `ServerGc`,
`GcLatencyMode`, and logical CPU count are reported by the measured application,
not inferred from the simulator. CPU/RSS fields retain their existing units.

`Build` identifies the executable hash, compiler/runtime and available Git
revision/status. Script builds also provide a build-time provenance manifest
containing source-tree and binary hashes. `Host` records readable CPU topology,
affinity and governor settings, relevant .NET environment overrides, and perf
permissions. Missing build manifests remain null rather than invented.

The final-state hash uses the same FNV mix as `DecisionHash`. Mix runtime count,
account-ready flag, then each instrument in configured order: instrument ID,
last book update ID, synchronized/warm/faulted flags (0/1), current/target/max-long/
max-short inventory, reserved buy/sell units; bid count and every bid price/quantity;
ask count and every ask price/quantity; bid then ask order ID, price, quantity,
filled quantity, numeric status and active flag. Status values are PendingNew=0,
PendingCancel=1, New=2, PartiallyFilled=3, Filled=4, Cancelled=5, Rejected=6,
Expired=7. Floating signal intermediates are checked by reference tests; the hash
covers final discrete state and is computed outside the measured frame path.

`paired-comparison.json` contains both language results and their correctness gate.
Ratios require complete equivalent output, sufficient measured duration/sample
count, and acceptable sender fidelity in every run. A P99 service-limit failure
still permits valid observed performance ratios, but does not pass capacity.
Smoke/trace/GC-diagnostic runs publish no ratios. Raw per-run results always remain
available. Ratios are CSharp/Rust with min/median/max over five runs per language;
allocation counters of different scopes are not divided into a misleading ratio.

# Benchmark report format (version 4)

`comparison.json` contains `SchemaVersion`, `RecordingId`, `HighestPassingSpeed` (nullable), and an array of `Speeds`. Each speed has five `Runs`, a `Passed` verdict, a failure reason, and `MeasurementWallOverheadPercent`. Every run is also saved as `run-N-summary.json`; both use the same version 4 JSON shape and PascalCase property names. Rust should emit these field names and units so the two implementations can be compared directly. `ReplayStateSource` is `synthetic` when no fixture is supplied and `fixture` otherwise. `FixtureSha256` is null for synthetic runs. Synthetic replay uses invented account, filter, and starting book state; its quote values are not historical results. C# selects each symbol's price tick and quantity step as 10 raised to the SBE exponents in its first quote or depth frame, then seeds its book from market observations. Compare C# and Rust decision hashes only when both use equivalent starting state and quote units.

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

`DecisionHash` covers every decision in recording order, including warm-up. Start with 64-bit FNV offset `14695981039346656037`. For each decision, mix these signed integer values in order: instrument ID, bid price ticks, bid quantity, bid action, ask price ticks, ask quantity, ask action. Each mix is `hash = (hash XOR value_as_unsigned_64_bits) * 1099511628211`, wrapping at 64 bits. Actions are `None=0`, `Place=1`, `Keep=2`, `Replace=3`, `Cancel=4`. Render the result as 16 lowercase hexadecimal digits. A fixture-free run checks decision repeatability across C# runs. A future Rust comparison needs matching synthetic initialization or a shared fixture before comparing decision hashes.

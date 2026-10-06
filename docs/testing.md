# Build and run a controlled C# / Rust comparison

The C# simulator supplies both applications. The default recording is
`fc401a92-7803-486e-8940-5db1eebc790c`. Scripts select its first **300,000 messages**;
`--max-messages N` overrides that and `--all` selects the entire recording.
The underlying simulator CLI has no entry limit unless one is provided.
The database recording is never shortened or modified.

All three scripts and the Rust recorder include the existing local PostgreSQL
connection. No connection prompt or environment variable is required.
`PG_CONNECTION` optionally overrides it; `PG_TEST_CONNECTION` optionally selects
another database for isolated integration tests.

## Prepare before closing development tools

These commands match this machine's repository locations. The scripts also work
from other directories and accept `CSHARP_ROOT` / `RUST_ROOT` or corresponding
`--csharp-root` / `--rust-root` arguments.

```bash
export CSHARP_ROOT="$HOME/RiderProjects/AssumptionZero"
export RUST_ROOT="$HOME/CLionProjects/assumption-zero"
export PATH="$HOME/.dotnet:$HOME/.cargo/bin:$PATH"

"$CSHARP_ROOT/scripts/run-csharp.sh" build
"$RUST_ROOT/scripts/run-rust.sh" build

# Optional verification before the measurement session. The PostgreSQL tests create
# a uniquely named schema and drop it afterwards; the recorded_messages table stays intact.
export RUST_APP_TEST="$RUST_ROOT/target/release/assumption-zero"
"$CSHARP_ROOT/scripts/run-csharp.sh" test
"$RUST_ROOT/scripts/run-rust.sh" test
python3 "$CSHARP_ROOT/scripts/test-scripts.py"

# Functional comparison: 1,000 recorded entries, one run per language, no performance verdict.
"$CSHARP_ROOT/scripts/run-simulator.sh" compare --smoke
```

Requirements: Linux, .NET 10 SDK, Rust/Cargo, Python 3, Bash, PostgreSQL access,
and `taskset`/`lscpu` from util-linux if pinning CPUs. Dependency downloads happen
during the build, not the benchmark. C# uses the functioning `$HOME/.dotnet/dotnet`
installation on this machine; `DOTNET_HOST_PATH` can override it.

The build scripts write a provenance manifest next to each executable. Reports
include its source-tree and binary hashes, build time, runtime/compiler versions,
and Git revision/status. A manually built executable can still run; its build
manifest is null if unavailable. Results also hash the selected recording bytes,
sequence numbers, timestamps, message types, and lengths.

## Quiet-machine session

Finish builds and tests first. Close your IDEs, browsers, background build/test
jobs, and other applications you do not want competing with the experiment.
Keep PostgreSQL available while each benchmark loads the recording. No database
queries occur during timed message delivery. The scripts do not kill unrelated
processes or change governors, turbo settings, scheduling priority, or kernel
permissions. An OS remains active, so timing is repeatable best effort rather
than perfectly deterministic.

Optional CPU pinning:

```bash
lscpu -e=CPU,CORE,SOCKET,ONLINE

# Verified on this machine: CPUs 2/3 share physical core 1.
# Give the simulator several other physical cores for its timing and I/O workers.
export SIMULATOR_CPUS=0,4,6,8,10,12,14
export APP_CPUS=2
```

Both variables must be set together when pinning. Scripts validate allowed CPU
sets and physical-core separation. C# and Rust receive exactly the same
application CPU set. This example measures one-core application performance.
Do not restrict this simulator to one logical CPU: the dedicated replay thread
spin-waits near deadlines while Kestrel uses other workers to deliver data.
The October 2 audit reproduced approximately 3 ms pre-receive batching with that
restriction. A wider simulator CPU set substantially reduced it in bounded
diagnostics. This does not guarantee that a full run passes its timing gates.
Choose the same larger application CPU set for both languages if that is the
experiment you intend. To use the normal scheduler instead:

```bash
unset APP_CPUS SIMULATOR_CPUS
```

## Preferred paired comparison

```bash
# Default: first 300,000 entries; one shared speed targeting ~60 s per replay.
"$CSHARP_ROOT/scripts/run-simulator.sh" compare

# Explicit prefix, or the entire recording:
"$CSHARP_ROOT/scripts/run-simulator.sh" compare --max-messages 100000
"$CSHARP_ROOT/scripts/run-simulator.sh" compare --all

# Original recorded timing (can take hours for large recordings):
"$CSHARP_ROOT/scripts/run-simulator.sh" compare --all --speed 1
```

Run these alternatives separately. The coordinator owns the simulator and
launches only one consumer at a time. Do not launch another simulator for these
commands. Each language gets an uninstrumented process-wall baseline and five
instrumented runs; language order alternates per pair. At approximately 60 seconds
per replay, a paired experiment takes roughly **12 minutes plus startup/reporting**.
The first 10% of messages are excluded from measured statistics, but included in
correctness hashes. Target duration scales time uniformly; it never edits the
recorded timestamps or removes gaps. `--all` changes selection only, not speed.

Threshold defaults are provisional: P99 <= 1,000 microseconds for send-to-decision
and send-to-frame-completion; simulator P99 send lateness <= 100 microseconds.
Set both explicitly before an experiment if you need different service targets:

```bash
"$CSHARP_ROOT/scripts/run-simulator.sh" compare \
  --p99-limit-us 1000 --replay-p99-lateness-us 100
```

Do not relax thresholds after seeing results to produce a passing claim. A
fixed-speed pass establishes only that tested load. The underlying CLI retains
`benchmark sweep` for deliberate capacity exploration; scripts do not launch it
automatically.

## Individual language runs

```bash
"$CSHARP_ROOT/scripts/run-csharp.sh" benchmark
"$RUST_ROOT/scripts/run-rust.sh" benchmark

"$CSHARP_ROOT/scripts/run-csharp.sh" benchmark --max-messages 100000
"$RUST_ROOT/scripts/run-rust.sh" benchmark --all
```

Each invocation starts its own shared C# simulator and writes independent results.
Use `compare` when you want the automated correctness gate, alternating order,
and paired ratios. Individual smoke checks are also available:

```bash
"$CSHARP_ROOT/scripts/run-csharp.sh" smoke
"$RUST_ROOT/scripts/run-rust.sh" smoke --max-messages 1000
```

## Manual simulator and client

Terminal 1 (keep PostgreSQL configuration in this terminal):

```bash
"$CSHARP_ROOT/scripts/run-simulator.sh" replay --max-messages 1000 --speed 1
```

After it prints `Listening`, run one client in terminal 2:

```bash
"$CSHARP_ROOT/scripts/run-csharp.sh" replay
# After C# exits, replay the same sequence from the beginning with Rust:
"$RUST_ROOT/scripts/run-rust.sh" replay
```

The server stays running, permits one active client, and starts at sequence 1
for each connection. Ctrl+C stops it. Put message limits and timing arguments on
the simulator; clients do not select the data. Manual runs do not produce paired
benchmark reports. Override addresses using simulator `--listen-url` and client
`--replay-url`.

## Fixtures and diagnostics

The market-only recording uses deterministic synthetic state in both languages.
Quote values are not historical trading results. For a recording with a matching
historical fixture, pass the same `--fixture PATH` to the coordinator, or to each
manual client. The bundled BTC fixture matches only `--recording demo`.

```bash
"$CSHARP_ROOT/scripts/run-simulator.sh" compare --recording demo --smoke \
  --fixture "$CSHARP_ROOT/AssumptionZero.App/replay-fixture.json" --trace
```

`--trace` writes ordered integer decision tuples and labels runs diagnostic;
it is disabled for normal measurements. `--fragment-size 1` exercises fragmented
WebSocket delivery. `--diagnostic` records individual GC pauses for C# only.
Diagnostic and smoke runs never establish capacity or publish performance ratios.

## Reports and exit codes

Every invocation prints its output directory. By default this is a unique folder
inside `$CSHARP_ROOT/benchmark-results`. `--out /absolute/empty/directory` chooses
a location; the runner refuses to overwrite existing results.

Paired runs produce:

- `paired-comparison.json`: dataset identity, equivalence verdict, validity gate,
  both languages' run summaries, and comparison metrics.
- `report.md`: readable results and run-to-run minimum/median/maximum values.
- `CSharp/comparison.json` and `Rust/comparison.json`: per-language results.
- Per-speed `run-N-summary.json`, `run-N-samples.csv`, GC diagnostics, stdout/stderr,
  and optional decision traces.

Performance ratios are withheld for unequal decisions/final states, incomplete
runs, insufficient duration/sample count, unreliable sender timing, and diagnostic
runs. Latency/CPU/memory ratios use CSharp/Rust: above 1 favors Rust. For throughput,
below 1 favors Rust. All results describe these implementations on this hardware,
not a general proof that one language is faster.

Managed allocation and Rust allocator allocation fields are intentionally
separate. Core processing is tested for zero allocations; the WebSocket transport
can allocate. Hardware counters are null with a reason when inaccessible. On this
host `perf_event_paranoid=4` blocks collection; scripts leave that setting intact.

Exit codes: `0` for successful smoke checks, valid completed diagnostics, or a
passing measured speed; `2` for failed capacity/correctness checks; `1` for setup
or execution errors; `130` for an interrupted coordinator. Short functional runs
should use `smoke`; a normal benchmark with 1,000 entries correctly fails the
10,000 measured-frame capacity requirement.

Use `--help` for all options and `--dry-run` to inspect the resolved command
without starting a benchmark. Credentials are not included in that command.

## Full order-cycle simulation

The 500,000-message, ten-minute-per-run order-cycle comparison has a separate
[closed-loop runbook](closed-loop-benchmark.md) and launcher. The commands above
continue to run the existing market-only benchmark.

# AssumptionZero.CSharp

AssumptionZero.CSharp is the C# implementation of **AssumptionZero**, a research project comparing C# and Rust in a low-latency, real-time system.

Both implementations will process the same market data, maintain an order book, run the same decision logic, and produce comparable outputs. The goal is to measure where time and CPU resources are spent, how each implementation behaves under load, and what changes at the tail of the latency distribution.

The project uses a simplified crypto trading system as its test case. Its purpose is to study system performance, not to develop a profitable trading strategy.

## How the system works

The main processing path is:

1. Receive a market data update.
2. Decode and validate it.
3. Update the local order book.
4. Calculate market signals.
5. Calculate a fair price, spread, and quote sizes.
6. Publish bid and ask place, keep, replace, or cancel intents. Order submission is outside this pipeline.

The initial directional score uses microprice imbalance, weighted book imbalance, best-level order flow imbalance, aggressive trade flow, and momentum. Volatility, liquidity, adverse fills, inventory, and maker fees affect spread and size. The same algorithms and input data will be used in the Rust implementation so that the language comparison is meaningful.

## Market data and simulation

The system supports two types of input:

- **Binance Spot Testnet SBE stream:** Live depth updates arrive over WebSocket in Simple Binary Encoding (SBE). This gives the system a real external data source and lets it be tested against changing market conditions.
- **Simulator and replay:** The simulator sends a finished recording over a local WebSocket with its original bursts and gaps. The standalone replay runs at 1×; benchmark commands can change the playback speed. There is no general-purpose synthetic traffic generator.

The SBE stream is used because it provides frequent binary market data updates. Replay provides a repeatable input sequence; testing maximum capacity with generated traffic is future work.

The application has three `EXECUTION_MODE` values: `SeedData` records live market messages, `NormalExecution` processes live messages, and `Replay` processes a simulator recording. In `Replay`, the market messages are always recorded data. `REPLAY_FIXTURE` selects how the application starts:

| `REPLAY_FIXTURE` | Initial account and order book | Use |
| --- | --- | --- |
| Set to a matching file | Supplied balances, instrument rules, and synchronized book snapshot | Inspect decisions for the recorded starting state |
| Unset | Estimated balances, rules, and book built from the first market updates | Measure processing performance; quote values are not historical results |

Live tests show how the application behaves with a real network connection. Simulator tests are the main basis for repeatable C# versus Rust comparisons.

## What will be measured

### Latency

Latency will be recorded for the complete processing path and for its main stages:

| Measurement | Start → end |
| --- | --- |
| Receive and decode | WebSocket message available to decoded update |
| Order book update | Decoded update to updated local book |
| Signal calculation | Updated book to calculated signals |
| Quote decision | Calculated signals to completed decision |
| End-to-end processing | Message available to completed decision |
| Queue delay | Event queued to event picked up by the consumer |

Reports will include **P50, P90, P95, P99, P99.9, and maximum latency**, together with the sample count. Tail latency matters because a system can have a good average while occasionally responding much more slowly.

When order submission is tested, local decision time, request preparation, network time, and exchange acknowledgement time will be reported separately. Exchange and network delays will not be presented as language runtime performance.

### Throughput and load

The tests will measure:

- Market data messages processed per second.
- Decisions produced per second.
- Maximum sustained input rate under a defined latency limit.
- Queue depth and time spent waiting in queues.
- Dropped or rejected events, if any.
- Behaviour during bursts and after a burst ends.

### CPU and memory

Where the operating system and hardware allow it, measurements will include:

- CPU usage and CPU time per processed event.
- CPU cycles and instructions.
- Cache misses and branch misses.
- Memory use.
- Allocations per event on the processing path.
- Garbage collection count, pause time, and effect on tail latency.
- System calls and scheduling delays.

Hardware counter results will be identified by machine and test conditions. A missing or unavailable counter will be marked as unavailable rather than estimated.

### Correctness

Performance only matters if both implementations produce the expected result. Tests will check:

- Order book state after a replay.
- Handling of gaps, reconnects, and invalid market data.
- Signal and quote outputs for the same input sequence.
- Whether repeated runs with the same simulator input produce the same decisions.
- Whether data is lost or processed out of order under load.

## Comparison method

The C# and Rust implementations will be tested with the same recorded input, strategy rules, configuration, and output checks.

Measurements will use Release builds. Each run will include a warm-up period before results are collected. Results will be reported across multiple runs, with the machine, CPU, operating system, runtime/compiler versions, input dataset, and test configuration recorded.

The project will compare both **latency** and **correctness**. An optimization that changes the system's decisions is not considered an equivalent implementation.

Live Binance measurements will be reported separately from controlled simulator results. Live traffic depends on the exchange, network, and time of day, so it cannot provide identical conditions for both languages.

### Repeatable C# benchmark reports

Build the whole solution in Release, then run the benchmark from the solution directory. A market-data recording can be measured without a fixture: the app uses synthetic account and starting book state while running the same processing and quote calculations. It selects each symbol's synthetic price and quantity units from its first recorded quote or depth frame. The resulting quote values are synthetic, so they cannot establish historical strategy correctness. The built-in `demo` is only a smoke test: its 120 messages are too few for a capacity verdict.

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build AssumptionZero.sln -c Release -m:1
dotnet AssumptionZero.Simulator/bin/Release/net10.0/AssumptionZero.Simulator.dll benchmark run \
  --recording RECORDING_UUID \
  --speed 1 --p99-limit-us 1000 --replay-p99-lateness-us 100 \
  --out /absolute/path/to/benchmark-results
```

The two limits above are examples, **not** recommended service targets. Choose them before running the experiment. Add `--fixture /absolute/path/to/matching-fixture.json` only when you need historically grounded quote decisions. `benchmark sweep` uses the same required options plus `--max-speed 16` (default 16). It measures 1×, doubles until failure or the maximum, then narrows a pass/fail boundary to about 10%. Every speed gets one uninstrumented process-wall baseline and five instrumented Release runs, so a single-speed 1× benchmark takes at least six times the recording's duration. The benchmark excludes the first 10% of frames as warm-up. A speed passes only if all five runs have identical decision hashes, complete frame delivery, acceptable simulator P99 send lateness, at least 30 measured seconds and 10,000 measured frames, and P99 send-to-decision and send-to-frame-completion times within the chosen limit. In fixture-free runs, the decision hash checks repeatability of synthetic calculations. A short or limited recording yields a failed capacity verdict rather than an invented maximum.

Results are saved in `comparison.json` and per-speed directories. Each run has a small versioned `run-N-summary.json`, a `run-N-samples.csv` with every frame, a `run-N-gc-pauses.json`, and application output/error files. CSV timestamps and stage durations are monotonic stopwatch ticks; use `ClockFrequency` in the summary to convert to nanoseconds. The summary reports P50, P90, P95, P99, P99.9, maximum, and sample count for processing stages, client receive-to-decision, simulator send-to-decision, and simulator send-to-frame-completion. A frame with no decision still appears in frame-processing and throughput counts. A trade frame with multiple decisions records the last decision timestamp and the number of decisions.

Application CPU time, average CPU cores used, CPU nanoseconds per measured frame, current and peak resident bytes, managed heap bytes, managed allocated bytes, allocation bytes per frame, Gen 0/1/2 counts, and cumulative GC pause time are reported separately from the simulator. Linux `perf` hardware counters cover the **whole application process**, including startup and warm-up, and are labelled with that scope. They are `null` with a reason when unavailable; on this host `perf_event_paranoid=4` blocks them. The process-wall measurement overhead estimate compares the five measured runs with one uninstrumented run at the same speed. It includes hardware-counter collection when available and is diagnostic because startup and replay scheduling contribute to wall time.

For individual GC pauses, add `--diagnostic`. This enables runtime GC events and marks frames whose processing interval overlaps a pause. Diagnostic runs are labelled separately and do not produce a capacity verdict because event tracing can change latency. To compare with Rust later, emit the same versioned summary fields and frame-sample columns, run on the same host with the same recording and fixture, and distinguish C# managed allocations from Rust allocator allocations. No exchange/network delay is included in the receive-to-decision language-path metric; send-to-decision deliberately includes local WebSocket buffering and receiver backlog.

The exact report fields, units, sample columns, and decision hash are defined in [Benchmark report format](docs/benchmark-schema.md).

## Replay your recorded messages from the beginning

Run these commands from the solution directory with the .NET 10 SDK installed. On this workspace, run `export PATH="$HOME/.dotnet:$PATH"` in each terminal first; `/usr/bin/dotnet` points to an incomplete system installation. The simulator reads a **completed recording** from PostgreSQL and sends every stored WebSocket message in sequence, starting with sequence number 1. It respects the recording's initial delay and original message timing.

First, set the same database connection string used while capturing the recording, then list its recording IDs:

```bash
export PG_CONNECTION='Host=localhost;Database=assumption_zero;Username=postgres;Password=YOUR_LOCAL_PASSWORD'
dotnet run --project AssumptionZero.Simulator -c Release -- list
```

Copy the UUID of the recording you want. In **terminal 1**, keep `PG_CONNECTION` set and start the simulator with that UUID:

```bash
dotnet run --project AssumptionZero.Simulator -c Release -- replay RECORDING_UUID
```

Wait for `Listening on http://127.0.0.1:5080/stream`. In **terminal 2**, connect the trading application:

```bash
EXECUTION_MODE=Replay dotnet run --project AssumptionZero.App -c Release
```

Each application connection starts the **entire recording again from its first stored message**; no skip or seek option is needed. The simulator remains running after the application reaches the end. Rerun the terminal 2 command to replay from the beginning again, or press Ctrl+C in terminal 1 to stop the server.

Without `REPLAY_FIXTURE`, the application creates deterministic account balances and instrument filters for all 100 configured USDT products. Each product gets an invented starting book from its first best-bid-ask message, or from observed depth changes if no best-bid-ask message arrives. Subsequent recorded depth and trades run through the same book, signal, risk, and quote calculations as live processing. The performance replay tolerates depth update-ID gaps because the invented starting book has no true synchronization point. This is a **performance replay**: the account, filters, starting depth, and resulting quote values are synthetic. Products with no usable book data cannot become ready. No orders are submitted or canceled.

For **historically grounded strategy decisions**, set `REPLAY_FIXTURE=/absolute/path/to/recording-fixture.json`. It must contain the actual account, filters, and synchronized book snapshot from the start of the recording; see `AssumptionZero.App/replay-fixture.json` for the format. The bundled BTC fixture matches only the built-in demo. This mode retains strict depth update-ID gap checks. Old market-only recordings do not contain these snapshots, so their quote values cannot be reconstructed accurately from the recording alone.

To make a recording first, follow [Capture a recording](#capture-a-recording) below. The optional database-free `demo` command is separate from recorded replay.

## Running the C# application

You need the .NET SDK used by the solution and a Binance Spot Testnet API key for live SBE data.

1. Visit [Binance Spot Testnet](https://testnet.binance.vision/).
2. Create an Ed25519 key pair and register its **public** key.
3. Set the issued API key in `BINANCE_APIKEY` and the path to its Ed25519 private key in `BINANCE_ED25519_PRIVATE_KEY_PATH`.
4. Restore and build the solution:

   ```bash
   dotnet restore
   dotnet build -c Release
   ```

5. Run the application project in live processing mode:

   ```bash
   EXECUTION_MODE=NormalExecution dotnet run --project AssumptionZero.App -c Release
   ```

The SBE WebSocket uses the API key in the `X-MBX-APIKEY` header. Startup signs read-only account and open-order requests with the private key, then subscribes to account events. The app waits for account reconciliation, a depth snapshot, and signal warm-up before producing new quote intents. It does not submit or cancel orders. `LOG_EVENTS` defaults to `false`; enabling it adds logging allocations.

**Do not commit an API key or private key to Git.** If a key has been committed, replace it through Binance Spot Testnet.

## Recording and replay

Commands below run from the solution directory in a shell with the .NET 10 SDK on `PATH`. Use Release builds for measurements. The quick-start commands above replay your own PostgreSQL recording from the first stored message.

### Capture a recording

Set `BINANCE_APIKEY` and `PG_CONNECTION` in your local environment. The database must contain the table from `AssumptionZero.App/init.sql`; run that script once for a new database, not against an already initialized table.

```bash
export PG_CONNECTION='Host=localhost;Database=assumption_zero;Username=postgres;Password=YOUR_LOCAL_PASSWORD'
EXECUTION_MODE=SeedData dotnet run --project AssumptionZero.App -c Release
```

Stop with Ctrl+C and wait for the queued database writes to finish before replaying. Each application run creates a new recording ID. Recording stores original binary WebSocket messages, sequence numbers starting at 1, and local receipt offsets in nanoseconds. It does not record text messages, ping/pong frames, or the original WebSocket fragmentation. The timestamp is taken in the recording callback after a complete message has been received and assembled, not at network-card arrival.

New recordings convert the monotonic stopwatch counter directly to nanoseconds. Old recordings remain compatible, but their existing 100 ns rounding cannot be undone. Nanosecond units do not guarantee nanosecond clock resolution or delivery accuracy.

### Select and serve a recording

Keep `PG_CONNECTION` set in the simulator terminal. List completed recordings, copy one UUID, then start its replay server:

```bash
dotnet run --project AssumptionZero.Simulator -c Release -- list
dotnet run --project AssumptionZero.Simulator -c Release -- replay RECORDING_UUID
```

`list` shows recording IDs, message counts, total payload bytes, and duration in nanoseconds, including the initial silence. `replay` requires a recording ID and loads the entire recording into memory before listening on `http://127.0.0.1:5080`. The database is not accessed during delivery. Allow additional memory for message objects, deadlines, and timing measurements as well as the payloads. A different listen address can be supplied as a third argument:

```bash
dotnet run --project AssumptionZero.Simulator -c Release -- replay RECORDING_UUID http://127.0.0.1:5081
```

Startup rejects empty/missing recordings, sequence gaps, negative or decreasing timestamps, message types other than binary/text, and payloads larger than 1 MB. Equal timestamps are valid. The recorder has no finalized flag: select a capture whose recording process has finished; a still-growing or truncated capture cannot be reliably identified from the existing table alone.

### Connect the application

In another terminal:

```bash
EXECUTION_MODE=Replay REPLAY_URL=ws://127.0.0.1:5080/stream \
  dotnet run --project AssumptionZero.App -c Release
```

Replay mode needs neither Binance credentials nor a database connection in the application. It uses the same receive loop, SBE decoder, and decision handlers as live mode. `REPLAY_URL` defaults to the address above. Without `REPLAY_FIXTURE`, it runs the all-product performance replay described above. To inspect historically grounded decisions, set `REPLAY_FIXTURE` to matching account and book state; a mismatch fails closed. Existing market-only recordings have no fills. `LOG_EVENTS` defaults to `false`; set it to `true` only for inspecting decoded market data. Set `MEASURE_ALLOCATIONS=1` to print allocated bytes over 100 ready decisions after 100 warm-up decisions; the counter includes other process allocations during that interval, so use the focused tests for the receive-to-decision assertion.

The simulator accepts `/stream?streams=...` URLs, but always sends the entire loaded recording: query parameters and the application's symbol list do not filter it. It accepts one active client; a concurrent connection receives HTTP 409. Every new connection starts from the beginning at 1× speed, including the original initial delay. After the last message the simulator closes normally and the application exits successfully. The simulator remains ready for another run. Ctrl+C stops the server; a client disconnect interrupts its current replay, including during long gaps.

### Timing and reports

Each message is scheduled against its absolute recorded offset from a fresh monotonic origin after connection setup. The dedicated sender sleeps during long gaps and busy-waits near deadlines; dense recordings may consume a CPU core. Send operations are serialized. If the sender falls behind, it sends every overdue event in order immediately and retains the original deadlines for future events. Catch-up can compress arrival intervals, so inspect the timing report before treating a run as equivalent load.

After each run the simulator prints a JSON report. All timing fields and percentile values are **nanoseconds**:

- `Completed`, `SentMessages`, and `SentBytes` describe completed send operations; an interrupted run is marked incomplete. A completed send is not confirmation that the consumer processed the message.
- `ExpectedDurationNanoseconds` includes the recording's initial gap. `ActualDurationNanoseconds` measures the timed sender loop through completion of the final send, excluding startup and the close handshake.
- `SendStartLateness` reports P50, P95, P99, P99.9 (`P999`), and maximum delay beyond each rounded clock deadline.
- `SendDuration` reports the same statistics for completed WebSocket sends, including time waiting for socket backpressure. Measurements are collected in preallocated arrays and summarized after sending.

Payload bytes, embedded exchange timestamps, message boundaries, and sequence order are preserved. Delivery timing is best effort: OS scheduling, GC, TCP buffering, consumer speed, and shared CPU contention can change actual receipt intervals. This replays recorded market data, not Binance authentication, dynamic subscriptions, auto-culling, or connection-control behavior. It cannot recreate events Binance never delivered to the recorder.

### Tests and loopback measurement

```bash
dotnet test AssumptionZero.sln -c Release
dotnet test AssumptionZero.Tests -c Release --filter TestCategory=Timing --logger 'console;verbosity=detailed'
```

The deterministic tests cover exact scheduling, formulas, account reservations, partial fills, sequence gaps, and repeated decisions. WebSocket tests cover original bytes, fragmented messages, concurrent-client rejection, disconnect cancellation, and the full receive-to-decision path. An isolated replay client measures managed allocations after warm-up for complete and fragmented frames and requires zero bytes over 100 decisions. The timing test warms the transport, then sends 1,000 messages spaced 100 microseconds apart. It separately reports server send lateness/duration, client receipt lateness against the same local monotonic clock, and absolute inter-receipt interval error. It has no hardware-dependent nanosecond pass threshold; this loopback fixture is diagnostic, not a throughput benchmark or a substitute for measurements with your real recording and consumer.

To include database integration, set `PG_TEST_CONNECTION` to a test PostgreSQL database before running the tests. The test creates a uniquely named schema, uses the existing table definition and recorder writer, verifies listing/selection/order/validation, and drops only that schema afterward. The account needs permission to create and drop schemas. Without this variable the database test is explicitly skipped.

## Research boundaries

The simulator is used to test processing speed, correctness, and behaviour under load. It cannot reproduce every detail of an exchange or network. Live testnet results include external delays and should not be read as pure C# performance measurements.

## References

- [Binance Spot Testnet](https://testnet.binance.vision/)
- [Binance Spot Testnet SBE market data streams](https://github.com/binance/binance-spot-api-docs/blob/master/testnet/sbe-market-data-streams.md)
- [Binance Spot Testnet API information](https://github.com/binance/binance-spot-api-docs/blob/master/testnet/general-info.md)

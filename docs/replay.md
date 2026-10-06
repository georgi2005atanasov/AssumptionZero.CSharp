# Recording and replay details

For the database-free demo and architecture overview, start with the [README](../README.md).

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

Without `REPLAY_FIXTURE`, the application creates deterministic account balances and instrument filters for the configured USDT products (currently five in C#). Each product gets an invented starting book from its first best-bid-ask message, or from observed depth changes if no best-bid-ask message arrives. Subsequent recorded depth and trades run through the same book, signal, risk, and quote calculations as live processing. The performance replay tolerates depth update-ID gaps because the invented starting book has no true synchronization point. This is a **performance replay**: the account, filters, starting depth, and resulting quote values are synthetic. Products with no usable book data cannot become ready. No orders are submitted or canceled.

For **historically grounded strategy decisions**, set `REPLAY_FIXTURE=/absolute/path/to/recording-fixture.json`. It must contain the actual account, filters, and synchronized book snapshot from the start of the recording; see `AssumptionZero.App/replay-fixture.json` for the format. The bundled BTC fixture matches only the built-in demo. This mode retains strict depth update-ID gap checks. Old market-only recordings do not contain these snapshots, so their quote values cannot be reconstructed accurately from the recording alone.

To make a recording first, follow [Capture a recording](#capture-a-recording) below. The optional database-free `demo` command is separate from recorded replay.


## Recording and replay

Commands below run from the solution directory in a shell with the .NET 10 SDK on `PATH`. Use Release builds for measurements. The commands below replay a PostgreSQL recording from the first stored message.

### Capture a recording

Set `BINANCE_API_KEY` and `PG_CONNECTION` in your local environment. The database must contain the table from `AssumptionZero.App/init.sql`; run that script once for a new database, not against an already initialized table.

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

Replay mode needs neither Binance credentials nor a database connection in the application. It uses the same receive loop, SBE decoder, and decision handlers as live mode. `REPLAY_URL` defaults to the address above. Without `REPLAY_FIXTURE`, it runs the configured-symbol performance replay described above. To inspect historically grounded decisions, set `REPLAY_FIXTURE` to matching account and book state; a mismatch fails closed. Existing market-only recordings have no fills. `LOG_EVENTS` defaults to `false`; set it to `true` only for inspecting decoded market data. Set `MEASURE_ALLOCATIONS=1` to print allocated bytes over 100 ready decisions after 100 warm-up decisions; the counter includes other process allocations during that interval, so use the focused tests for the receive-to-decision assertion.

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


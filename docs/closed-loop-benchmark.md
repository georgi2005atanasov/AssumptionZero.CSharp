# Controlled exchange comparison

This benchmark adds a complete local order cycle to the recorded SBE replay:
market update → strategy/risk → reserve funds → send order over WebSocket →
exchange acknowledgement/fill/rejection → account and inventory update → next decision.
Both clients connect to the same C# exchange implementation. No Binance endpoint,
API key, or live account is used. PostgreSQL only supplies the saved recording,
which is loaded before client measurement.

## Prepare, then measure

Build while other applications may still be open:

```bash
/home/sparrow/RiderProjects/AssumptionZero/scripts/run-closed-loop-benchmark.sh prepare
```

Then close applications as desired and run from the terminal:

```bash
/home/sparrow/RiderProjects/AssumptionZero/scripts/run-closed-loop-benchmark.sh run \
  --max-messages 500000 \
  --target-duration-seconds 600 \
  --app-cpus 2 \
  --simulator-cpus 0,4,6,8,10,12,14
```

The CPU IDs are for the development machine. The launcher checks available CPUs
and rejects sharing physical cores, including SMT siblings, between client and
simulator. Omit affinity options on another machine or choose its CPU topology.
`PG_CONNECTION`, `RUST_ROOT`, and `DOTNET_HOST_PATH` can override local setup.
The launcher preserves explicit database settings; its local default uses the
installed database at 172.17.0.2. It never prints connection credentials.

Default workload: **500,000 total messages per run**, first **50,000 warmup**,
then **450,000 measured**. The original relative arrival times are scaled to
**600 seconds for the complete replay**, including warmup. Warmup duration is
therefore determined by the recording, not fixed at exactly 60 seconds.

Before timing, each language replays the whole prefix twice using deterministic
barriers. All four event/command hashes and final account/order states must match.
Then each language runs one timing-disabled baseline, followed by five measured
runs each, alternating C#→Rust and Rust→C#. The twelve paced runs take about
**two hours**, plus preflight and cleanup. Each session starts a fresh process,
exchange ledger, and order ID sequence.

`prepare` saves hashes of source/configuration files and binaries. `run` refuses
changed builds until preparation is repeated. It does not compile during the
benchmark. Results go to a unique `benchmark-results/...-closed-loop-*` directory,
with a `.tar.gz` archive beside it. Return that archive for analysis. A failed run
has a `-FAILED.tar.gz` name and no successful comparison summary.

For a quick functional check, without performance conclusions:

```bash
/home/sparrow/RiderProjects/AssumptionZero/scripts/run-closed-loop-benchmark.sh check \
  --max-messages 10000
```

Use `--help` or `run --dry-run` to inspect options. `--recording demo
--max-messages 120` uses an in-memory fixture and needs no database.

## Execution model

There are separate loopback WebSockets for market data and order/report traffic.
One client state owner processes the book, signals, risk reservations, orders,
execution reports, and balances. Socket tasks never mutate that state. C# uses
bounded SPSC rings; Rust uses bounded Tokio channels with one producer and one
consumer per channel. Capacity is 8,192 packets. Overflow, disconnection, invalid
sequence, or failed reconciliation aborts the session. There is no reconnect or
recovery protocol in this version. Identical immediately repeated reports are
idempotent; stale/out-of-order or conflicting repeats fail closed.

In **deterministic mode**, each market step has a report watermark and a command
batch barrier. Reports due at that step are applied before its market update.
Synthetic delays use replay steps. This checks reproducibility; it is not a
latency benchmark.

In **paced asynchronous mode**, the market loop does not wait for each order.
A separate exchange loop advances pending executions and processes commands.
Delays use monotonic milliseconds. TCP_NODELAY is enabled for the clients.
After the last market frame, new decisions stop, outstanding commands drain,
scheduled fills complete, and resting orders are cancelled. Both ledgers must
agree with zero exchange holds, client reservations, or active orders.

The `mixed` scenario acknowledges valid placements immediately, rejects every
eighth placement, leaves every fourth placement resting, and fills the other
orders after four units. Even-numbered eligible placements with quantity > 1
partially fill after two units. Cancellation may win or lose a fill race.
Replacement is cancel-confirm-then-place, never an assumed atomic replace.
The `rest` scenario leaves accepted orders resting until cancellation/drain.
The fee is 0.1% of quote notional, charged in quote currency. Buys reserve fees
as well as principal. Exchange accounting is independent of client risk code.

Delays are scheduled minimums, not guaranteed wall-clock latency: socket queues,
exchange polling, and OS scheduling add time. Fills use submitted prices and
scripted quantities; there is no historical queue-position or matching model.

## What the measurements mean

Per-client JSON includes process CPU, RSS after reconciliation but before report
construction, allocation bytes, frame processing time, report application time,
server-event creation to client application time, command creation to response
application time, queue high-water estimates, workload counts, readiness reasons,
and final state. C# also records collection counts. Separate drain CPU/time fields
keep final cleanup out of the steady measurement interval. Sample CSVs preserve
observation order and accompany percentile summaries.

CPU includes socket, JSON/protocol, hashing, queue, and runtime work. Frame samples
cover market decode through strategy/risk/command creation; report and command
latencies include different portions of the round trip. Command timing ends at
the first acknowledgement, rejection, or cancel response, not at full fill.
Server-to-application timestamps use the same Linux monotonic clock on this host.
Do not interpret simulated exchange delay as strategy computation time.

Baselines disable per-event timing samples and Rust allocation counting, while
retaining reconciliation, hashes, workload counters, and boundary clock reads. They quantify the added
measurement cost, not all cost of the simulated harness. C# managed allocation
bytes and Rust requested allocator bytes are different measures, not a universal
cross-language allocation ratio. RSS contains runtime and sample-buffer memory;
the baseline helps expose that contribution.

Asynchronous scheduling can change the number and order of cancellations, fills,
and later decisions. **Equal market-frame count does not guarantee equal work.**
Compare accepted orders, fill events, commands, reports, ready decisions, sender
lateness, and queue occupancy before CPU or latency conclusions. Raw results are
saved, but the coordinator deliberately does not publish speedup ratios or a
rewrite verdict. A successful exit proves protocol completion/reconciliation and
preflight equivalence; it is not automatic certification of a performance claim.

## Recording and readiness limits

The 100 configured symbols remain in place. The selected recording contains 99;
LRCUSDT has no messages. Coverage counts are written for every symbol. The saved
feed contains quotes and depth changes, not trades or original depth snapshots.
Starting books are estimated using the existing replay model. Balances start at
1 billion USDT and 100 units of every base asset; precision comes from observed
SBE exponents, not fetched exchange filters. There is no order minimum-notional
rule beyond this declared synthetic setup.

Readiness requires a valid book, 100 OFI observations, and five seconds of event
time since its signal warmup began. An estimated book can lose valid depth or
become crossed and be reseeded; reseeding resets signal warmup. The final number
of ready instruments is not the number that traded during the session. Reports
include ready, book-unready, warming, and signal-fault decision counts for each
symbol; they do not force readiness to improve apparent workload coverage.

Development validation of 500,000 messages produced matching C#/Rust traces,
75,801 accepted orders and 74,634 fill events per deterministic replay, with
80,495 ready decisions and no signal-memory faults. Five instruments were ready
at the endpoint. These are correctness observations, not quiet-machine timing
results. Full order cycles improve the evidence beyond the old market-only
report, but still do not establish production execution quality, maximum
capacity, or a reason to rewrite an adequate C# system.

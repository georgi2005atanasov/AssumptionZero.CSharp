# AssumptionZero — C#

A market-processing application that decodes Binance SBE messages, maintains
instrument books and signals, and produces quote intents. Replay and live modes
produce intents; the local simulated exchange also exercises order submission,
acknowledgements, fills, and cancellations.

This repository contains the C# application, the shared simulator, and integration
tests. The [Rust implementation](https://github.com/georgi2005atanasov/assumption_zero)
uses the same simulator and fixture format.

## Get the source

```bash
git clone https://github.com/georgi2005atanasov/AssumptionZero.CSharp.git
cd AssumptionZero.CSharp
```

Choose Docker for a self-contained demo and C# tests, or install the .NET 10 SDK
and OpenSSL for native development. Downloads require network access; the demo
and regular tests use local fixtures and need no Binance credentials or database.

## Start the demo with Docker

Install Docker with Compose and make sure `docker info` succeeds. From the repository
root, run:

```bash
docker compose up --build --abort-on-container-exit --exit-code-from app
```

This builds the .NET applications, starts the simulator, waits for it to listen,
and connects the application using the bundled BTCUSDT starting-state fixture.
It needs **no API keys, PostgreSQL, Rust installation, or host .NET SDK**. The first
build downloads container images and NuGet packages. No host ports are needed.

Expected output includes:

```text
Replay complete: 120 decisions, ... with place/replace/cancel intents.
Products: 1 with a book, 1 ready of 1 configured.
```

The application exits successfully after the replay and Compose stops the simulator.
Run the same command again to repeat it. Remove the stopped containers with:

```bash
docker compose down
```

## Run the tests

Using Docker:

```bash
docker compose run --build --rm tests
```

The tests cover scheduling, SBE validation, book and signal calculations, quote
rules, reservations, order-state transitions, fragmented WebSockets, repeated
replays, signing, and reports. Focused tests check zero managed allocations over
warmed processing windows; this is not a claim that the whole process allocates
nothing. Database and Rust integration tests skip when their configuration is absent.

To include the C# PostgreSQL integration test:

```bash
docker compose run --build --rm database-tests
```

This starts an isolated local PostgreSQL service. The test creates and removes its
own schema. Stop the database with `docker compose --profile database down`; its
named volume persists across restarts.

## Run without Docker

Use the .NET 10 SDK and OpenSSL on `PATH`. Commands below use Bash on Linux from
the repository root.

```bash
export DOTNET_HOST_PATH="$(command -v dotnet)"
dotnet build AssumptionZero.sln -c Release
dotnet test AssumptionZero.Tests/AssumptionZero.Tests.csproj -c Release --no-build
```

Build the entire solution first: some tests launch the Release application
executable. If the SDK is installed in `$HOME/.dotnet`, add that directory to `PATH`
before running these commands. Check `dotnet --list-sdks` for a 10.x SDK.
`DOTNET_HOST_PATH` tells tests and launchers which .NET executable to use.

In terminal 1:

```bash
dotnet AssumptionZero.Simulator/bin/Release/net10.0/AssumptionZero.Simulator.dll demo
```

After the simulator prints its listening address, run in terminal 2:

```bash
EXECUTION_MODE=Replay \
REPLAY_URL=ws://127.0.0.1:5080/stream \
REPLAY_FIXTURE="$PWD/AssumptionZero.App/replay-fixture.json" \
  dotnet AssumptionZero.App/bin/Release/net10.0/AssumptionZero.App.dll
```

The application exits after replay; the simulator remains available for another
connection. Stop the simulator with Ctrl+C.

## Live data and recorded replays

| `EXECUTION_MODE` | Behavior | Requirements |
| --- | --- | --- |
| `Replay` (default) | Process messages from the local simulator | Running simulator; a matching fixture is optional |
| `SimulatedExchange` | Controlled local order/report cycle | Use the closed-loop launcher |
| `SeedData` | Record live SBE market messages into PostgreSQL | Testnet API key and database |
| `NormalExecution` | Load instrument/account/book snapshots, subscribe to account events, and process live market data | Testnet API key and matching Ed25519 private key |

For live processing, register an Ed25519 public key on
[Binance Spot Testnet](https://testnet.binance.vision/) and set the corresponding
API key and private-key file path:

```bash
export BINANCE_API_KEY='YOUR_TESTNET_API_KEY'
export BINANCE_ED25519_PRIVATE_KEY_PATH='/absolute/path/to/your-private-key.pem'
EXECUTION_MODE=NormalExecution \
  dotnet AssumptionZero.App/bin/Release/net10.0/AssumptionZero.App.dll
```

The signing helper uses OpenSSL and expects a private PEM file without a passphrase.
In an IDE, set the same environment variables in the run configuration. Live mode
requires both explicitly. Local signing and replay tests do not establish live
Binance connectivity or account permissions; check those with the live run.

To capture data, you can start the optional local database:

```bash
docker compose up -d --wait database
export PG_CONNECTION='Host=localhost;Port=5433;Database=assumption_zero;Username=postgres;Password=local_test_password'
EXECUTION_MODE=SeedData \
  dotnet AssumptionZero.App/bin/Release/net10.0/AssumptionZero.App.dll
```

Keep `BINANCE_API_KEY` set for capture. Compose initializes the recording table on
the first start of its database volume. For your own database, apply
[init.sql](AssumptionZero.App/init.sql) once to a new database instead. Stop capture
with Ctrl+C and let pending writes finish before replaying it. Change `POSTGRES_PORT`
when starting Compose if host port 5433 is already occupied, and update your connection
string accordingly. The built-in database defaults elsewhere in the code are specific
to the development machine; set `PG_CONNECTION` for your environment.

See [recording and replay details](docs/replay.md) for listing recordings, replaying
from sequence one, fixture selection, timing, and optional PostgreSQL integration
tests. Set `PG_TEST_CONNECTION` separately to enable database tests outside Docker.

## Run cross-language tests

Install Rust through rustup, then build the Rust application. Set `RUST_ROOT` to
an absolute checkout path of your choice; commands here run from the C# root.

```bash
export RUST_ROOT="$HOME/projects/assumption_zero"
git clone https://github.com/georgi2005atanasov/assumption_zero.git "$RUST_ROOT"
(cd "$RUST_ROOT" && cargo +stable build --release --locked)
(cd "$RUST_ROOT" && cargo +stable test --release --locked)
export RUST_APP_TEST="$RUST_ROOT/target/release/assumption-zero"
export DOTNET_HOST_PATH="$(command -v dotnet)"
dotnet build AssumptionZero.sln -c Release
dotnet test AssumptionZero.Tests/AssumptionZero.Tests.csproj -c Release --no-build
```

Skip the clone if the repository already exists. Rust's own tests run independently
of C#. Setting `RUST_APP_TEST` enables the C# suite's Rust interoperability checks,
including replay, live-adapter fixtures, and simulated order handling.

To include both database integration tests, start the database described above,
then rerun the suite with both `RUST_APP_TEST` and `PG_TEST_CONNECTION` set:

```bash
export PG_TEST_CONNECTION='Host=localhost;Port=5433;Database=assumption_zero;Username=postgres;Password=local_test_password'
dotnet test AssumptionZero.Tests/AssumptionZero.Tests.csproj -c Release --no-build
```

Use a test database whose account can create and drop schemas. The tests create
and remove their own schemas. `PG_CONNECTION` configures applications and the
simulator; `PG_TEST_CONNECTION` separately enables database tests. Missing optional
configuration produces skipped tests, so inspect the test summary.

## Run the local order-cycle demo

After building both projects above, this command starts the exchange and clients,
checks deterministic results, and exercises asynchronous settlement without a
database or Binance credentials:

```bash
dotnet AssumptionZero.Simulator/bin/Release/net10.0/AssumptionZero.Simulator.dll closed-loop \
  --recording demo --max-messages 120 --target-duration-seconds 1 \
  --repetitions 1 --scenario mixed --language both --rust-root "$RUST_ROOT"
```

The runner prints the results directory and reconciliation status, then exits.
Use `--scenario rest` to exercise resting orders. `--language CSharp` runs only the
C# client, but the runner still needs the Rust checkout for `src/symbols.json`.
Keep `DOTNET_HOST_PATH` set as above.

## Project layout

| Project | Responsibility |
| --- | --- |
| `AssumptionZero.App` | Startup, mode selection, replay and session orchestration |
| `AssumptionZero.Application` | Service contracts, instrument runtime, account risk and reservations |
| `AssumptionZero.Domain` | Books, signals, configuration, and strategy calculations |
| `AssumptionZero.Infrastructure` | Exchange adapters, decoding, persistence, and reporting |
| `AssumptionZero.Simulator` | Demo and recorded playback, controlled local exchange |
| `AssumptionZero.Tests` | Unit and integration tests |

## Further documentation

- [Recording and replay](docs/replay.md)
- [Benchmark setup and execution](docs/testing.md)
- [Controlled exchange runbook](docs/closed-loop-benchmark.md)
- [Benchmark report fields](docs/benchmark-schema.md)
- [Recorded comparison report](docs/benchmark-accepted-2026-10-02.md)

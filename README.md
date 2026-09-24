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
6. Record the decision and measurement data.

The signals include order book imbalance, order flow, trade flow, volume, volatility, momentum, liquidity, and inventory exposure. The same algorithms and input data will be used in the Rust implementation so that the language comparison is meaningful.

## Market data and simulation

The system supports two types of input:

- **Binance Spot Testnet SBE stream:** Live depth updates arrive over WebSocket in Simple Binary Encoding (SBE). This gives the system a real external data source and lets it be tested against changing market conditions.
- **Simulator and replay:** Controlled inputs make tests repeatable. They can also generate rates and bursts that the live testnet may not provide.

The SBE stream is used because it provides frequent binary market data updates. It does not guarantee enough traffic to test the system's maximum capacity; the simulator is used for that.

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

Reports will include **P50, P95, P99, P99.9, and maximum latency**, together with the sample count. Tail latency matters because a system can have a good average while occasionally responding much more slowly.

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

## Running the C# application

You need the .NET SDK used by the solution and a Binance Spot Testnet API key for live SBE data.

1. Visit [Binance Spot Testnet](https://testnet.binance.vision/).
2. Create an Ed25519 key pair and register its **public** key.
3. Copy the issued API key into the configuration at the beginning of the application's `Program.cs`.
4. Restore and build the solution:

   ```bash
   dotnet restore
   dotnet build -c Release
   ```

5. Run the application project:

   ```bash
   dotnet run --project AssumptionZero.App -c Release
   ```

The SBE WebSocket connection uses the API key in the `X-MBX-APIKEY` header. It does not need the Ed25519 private key or a request signature. Trading requests, if enabled, have different authentication requirements.

**Do not commit an API key or private key to Git.** If a key has been committed, replace it through Binance Spot Testnet.

## Research boundaries

The simulator is used to test processing speed, correctness, and behaviour under load. It cannot reproduce every detail of an exchange or network. Live testnet results include external delays and should not be read as pure C# performance measurements.

## References

- [Binance Spot Testnet](https://testnet.binance.vision/)
- [Binance Spot Testnet SBE market data streams](https://github.com/binance/binance-spot-api-docs/blob/master/testnet/sbe-market-data-streams.md)
- [Binance Spot Testnet API information](https://github.com/binance/binance-spot-api-docs/blob/master/testnet/general-info.md)

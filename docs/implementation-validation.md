# Implementation validation — 2026-10-01

The Rust application is in `/home/sparrow/CLionProjects/assumption-zero`.
Both languages use the existing C# simulator. Authoritative performance testing
is intentionally left for the user's quiet-machine session; see [testing.md](testing.md).

## Completed checks

- C# Release build: zero warnings/errors.
- C# tests: 42 passed, zero skipped, including isolated PostgreSQL and Rust interoperability tests.
- Rust Release tests: 14 passed, including local live-bootstrap protocol fixtures,
  queue fault handling, C# decimal vectors, formulas, reservations, fills, malformed
  SBE, and zero allocator bytes on the warmed core path.
- Rust Clippy, all Release targets, warnings denied: passed.
- Bash syntax checks and seven script argument-contract tests: passed.
- Fragmented demo replay with shared historical fixture: identical per-decision
  tuples and final state.
- Rust SeedData against a local WebSocket fixture: all ten original binary messages
  persisted in the shared PostgreSQL format before capture reported remote closure.
  The test used and removed a unique schema.
- Interrupted coordinator: exit 130, no surviving child application.
- Built-in local PostgreSQL defaults verified with `PG_CONNECTION` and
  `PG_TEST_CONNECTION` unset: recording listing, all 42 C# tests, all 14 Rust
  tests, and a fresh 1,000-entry paired smoke comparison passed. Rust SeedData
  also connected and prepared its recording INSERT using its own default before
  stopping at the missing Binance API key, without contacting the external feed.
  Smoke report: `benchmark-results/20261001T143608Z-simulator-compare-MbHQ8l/paired-comparison.json`.

## Bounded real-recording comparison

Recording: `fc401a92-7803-486e-8940-5db1eebc790c`; prefix: 1,000 messages;
payload: 104,636 bytes; original duration: approximately 41.776 seconds.
Smoke playback: 1000x. Simulator pinned to CPU 0, application pinned to CPU 2;
fragment size seven bytes; decision tracing enabled.

Both applications received all 1,000 frames and produced:

- 637 identical decision tuples, 83 actionable.
- Decision hash `45480090168b6a61`.
- Final discrete-state hash `55e78c1bfa675cad`.
- 74 initialized books, two ready instruments out of 100 configured.

The saved smoke report is in
`benchmark-results/20261001T135747Z-simulator-compare-XpPx2L/paired-comparison.json`.
Every trace tuple was compared byte-for-byte. Summary metadata confirmed one
logical CPU per application, C# workstation GC, and null GC/managed counters for Rust.

The normal five-run coordinator path was also exercised with only 1,000 messages
at 1000x, including both uninstrumented baselines and alternating language order.
All ten measured runs had identical decision and state hashes. It correctly
returned exit 2 and withheld performance ratios because the measured window was
under 30 seconds and under 10,000 frames; some latency/fidelity gates also failed.
That report is in
`benchmark-results/20261001T135822Z-simulator-compare-caa89W/paired-comparison.json`.

These are functional checks, not evidence of a Rust performance advantage.
No 300,000-entry or whole-recording performance run was performed. External
Binance testnet connectivity was not exercised; live adapters were validated
with local protocol fixtures and deterministic signing tests.

#!/usr/bin/env bash
# Shared implementation for the three entrypoints. No privileged host changes.
# Existing local PostgreSQL database; callers may optionally override it.
export PG_CONNECTION="${PG_CONNECTION:-Host=172.17.0.3;Database=assumption_zero;Username=postgres;Password=mysecretpassword;Trust Server Certificate=true;}"
az_fail() { echo "Error: $*" >&2; exit 1; }
az_help() {
  cat <<'HELP'
Usage: run-{csharp,rust,simulator}.sh MODE [OPTIONS]
  build        Build Release binaries (simulator builds the C# solution).
  test         Run language tests, including isolated C# DB tests on the local database.
  replay       Start the simulator, or connect the selected application to it.
  benchmark    Benchmark one language using an automatically managed simulator.
  smoke        One quick instrumented run, no capacity verdict (1000 entries).
  compare      Simulator entrypoint only: alternate C# and Rust measurements.
  list         Simulator entrypoint only: list recordings.
Options:
  --max-messages N       Prefix length; default 300000 (smoke mode: 1000).
  --all                  Whole recording; mutually exclusive with --max-messages.
  --recording UUID|demo  Default fc401a92-7803-486e-8940-5db1eebc790c.
  --speed N             Explicit replay multiplier; --speed 1 keeps original timing.
  --target-duration-seconds N   Default 60; mutually exclusive with --speed.
  --out DIR             Empty results directory; default is a unique directory.
  --fixture PATH        Shared historical starting state; default synthetic.
  --rust-root DIR / --csharp-root DIR   Override repository locations.
  --rust-app PATH / --csharp-app PATH   Override Release binaries.
  --app-cpus LIST / --simulator-cpus LIST  Optional physical-core-separated affinity.
  --replay-url URL       Client URL (default ws://127.0.0.1:5080/stream).
  --listen-url URL       Standalone simulator URL (default http://127.0.0.1:5080).
  --p99-limit-us N / --replay-p99-lateness-us N   Defaults 1000 / 100.
  --smoke                Make compare a quick functional check (1000 entries).
  --trace / --diagnostic / --fragment-size N    Correctness/diagnostic options.
  --dry-run              Print the resolved command without executing it.
  --help                 Show this help.
The local PostgreSQL connection is built in; no environment setup is needed.
Optional overrides: PG_CONNECTION, PG_TEST_CONNECTION, DOTNET_HOST_PATH, CSHARP_ROOT,
RUST_ROOT, APP_CPUS, SIMULATOR_CPUS. Credentials are never printed by the scripts.
Build before quiet-machine measurements. Benchmark/compare own their simulator;
do not start another. Manual clients take --replay-url and --fixture; put message
limits/timing options on the simulator. PostgreSQL is used only to load recordings.
HELP
}
az_main() {
  local entry=$1; shift
  local mode=${1:---help}
  if [[ $mode == --help || $mode == -h ]]; then az_help; return; fi
  shift
  local recording=fc401a92-7803-486e-8940-5db1eebc790c limit= all=0 speed= duration= output= fixture= dry=0 smoke=0
  local rust_app= csharp_app= app_cpus=${APP_CPUS:-} sim_cpus=${SIMULATOR_CPUS:-}
  local replay_url=${REPLAY_URL:-ws://127.0.0.1:5080/stream} listen_url=http://127.0.0.1:5080
  local -a extra=() cmd=()
  local -A seen=()
  local default_rust_root=/home/sparrow/CLionProjects/assumption-zero
  export RUST_ROOT="${RUST_ROOT:-$default_rust_root}"
  if [[ ! -f "$RUST_ROOT/Cargo.toml" && -f "$default_rust_root/Cargo.toml" ]]; then
    export RUST_ROOT=$default_rust_root
  fi
  while (($#)); do
    local key=$1; shift
    [[ -z ${seen[$key]:-} ]] || az_fail "Duplicate option $key"
    seen[$key]=1
    case "$key" in
      --help|-h) az_help; return ;;
      --all) all=1 ;;
      --dry-run) dry=1 ;;
      --smoke) smoke=1 ;;
      --trace|--diagnostic) extra+=("$key") ;;
      --max-messages|--recording|--speed|--target-duration-seconds|--out|--fixture|--rust-root|--csharp-root|--rust-app|--csharp-app|--app-cpus|--simulator-cpus|--replay-url|--listen-url|--p99-limit-us|--replay-p99-lateness-us|--fragment-size)
        (($#)) || az_fail "$key needs a value"
        local value=$1; shift
        case "$key" in
          --max-messages) limit=$value ;;
          --recording) recording=$value ;;
          --speed) speed=$value ;;
          --target-duration-seconds) duration=$value ;;
          --out) output=$value ;;
          --fixture) fixture=$value ;;
          --rust-root) export RUST_ROOT=$value ;;
          --csharp-root) export CSHARP_ROOT=$value ;;
          --rust-app) rust_app=$value ;;
          --csharp-app) csharp_app=$value ;;
          --app-cpus) app_cpus=$value ;;
          --simulator-cpus) sim_cpus=$value ;;
          --replay-url) replay_url=$value ;;
          --listen-url) listen_url=$value ;;
          *) extra+=("$key" "$value") ;;
        esac ;;
      *) az_fail "Unknown option $key" ;;
    esac
  done
  [[ -z $limit || $limit =~ ^[1-9][0-9]*$ ]] || az_fail '--max-messages must be a positive integer'
  [[ -z $limit || $all == 0 ]] || az_fail 'Choose --all or --max-messages, not both'
  [[ -z $speed || -z $duration ]] || az_fail 'Choose --speed or --target-duration-seconds, not both'
  [[ $mode != smoke ]] || smoke=1
  if [[ -z $limit && $all == 0 ]]; then if ((smoke)); then limit=1000; else limit=300000; fi; fi
  local dotnet=${DOTNET_HOST_PATH:-}
  if [[ -z $dotnet ]]; then
    if [[ -x /home/sparrow/.dotnet/dotnet ]]; then dotnet=/home/sparrow/.dotnet/dotnet;
    elif [[ -x "$HOME/.dotnet/dotnet" ]]; then dotnet="$HOME/.dotnet/dotnet";
    else dotnet=$(command -v dotnet || true); fi
  fi
  local cargo_bin=cargo
  local -a cargo_command=()
  if [[ $entry == rust && ( $mode == build || $mode == test ) ]]; then
    cargo_bin=$(command -v cargo || true)
    if [[ -z $cargo_bin ]]; then
      for candidate in "$HOME/.cargo/bin/cargo" /home/sparrow/.cargo/bin/cargo; do
        if [[ -x $candidate ]]; then cargo_bin=$candidate; break; fi
      done
    fi
    [[ -n $cargo_bin ]] || az_fail 'Rust/Cargo was not found'
    cargo_command=("$cargo_bin")
    local rustup_bin="$(dirname -- "$cargo_bin")/rustup"
    if [[ -x $rustup_bin ]]; then
      # --manifest-path does not select a rustup toolchain from another directory.
      # Select stable explicitly without modifying the user's global default.
      cargo_command+=(+stable)
      if (( ! dry )) && ! "$rustup_bin" run stable cargo --version >/dev/null 2>&1; then
        "$rustup_bin" toolchain install stable --profile minimal || az_fail 'Could not install the stable Rust toolchain'
      fi
    fi
  else
    [[ -n $dotnet ]] || az_fail 'Install the .NET 10 SDK or set DOTNET_HOST_PATH'
  fi
  export DOTNET_HOST_PATH=$dotnet
  local simulator="$CSHARP_ROOT/AssumptionZero.Simulator/bin/Release/net10.0/AssumptionZero.Simulator.dll"
  csharp_app=${csharp_app:-$CSHARP_ROOT/AssumptionZero.App/bin/Release/net10.0/AssumptionZero.App.dll}
  rust_app=${rust_app:-$RUST_ROOT/target/release/assumption-zero}
  case "$mode" in
    build)
      if [[ $entry == rust ]]; then cmd=("${cargo_command[@]}" build --locked --release --manifest-path "$RUST_ROOT/Cargo.toml" --target-dir "$RUST_ROOT/target"); else cmd=("$dotnet" build "$CSHARP_ROOT/AssumptionZero.sln" -c Release -m:1); fi
      if ((dry)); then printf '%q ' "${cmd[@]}"; printf '\n'; return; fi
      "${cmd[@]}"
      if [[ $entry == rust ]]; then python3 "$CSHARP_ROOT/scripts/environment.py" manifest "$RUST_ROOT" "$rust_app";
      else python3 "$CSHARP_ROOT/scripts/environment.py" manifest "$CSHARP_ROOT" "$csharp_app" "$simulator"; fi
      return ;;
    test)
      export PG_TEST_CONNECTION="${PG_TEST_CONNECTION:-$PG_CONNECTION}"
      if [[ $entry == rust ]]; then cmd=("${cargo_command[@]}" test --locked --release --manifest-path "$RUST_ROOT/Cargo.toml" --target-dir "$RUST_ROOT/target"); else cmd=("$dotnet" test "$CSHARP_ROOT/AssumptionZero.sln" -c Release -m:1); fi ;;
    replay)
      if [[ $entry == simulator ]]; then
        if [[ $recording == demo ]]; then cmd=("$dotnet" "$simulator" demo "$listen_url"); else cmd=("$dotnet" "$simulator" replay "$recording" "$listen_url"); fi
        if ((all)); then cmd+=(--all); else cmd+=(--max-messages "$limit"); fi
        if [[ -n $speed ]]; then cmd+=(--speed "$speed"); else cmd+=(--target-duration-seconds "${duration:-60}"); fi
      else
        [[ -z ${seen[--max-messages]:-}${seen[--all]:-}$speed$duration ]] || az_fail 'Put message limits and timing on the simulator, not a manual client'
        [[ -z $fixture ]] || export REPLAY_FIXTURE=$fixture
        [[ -n $fixture ]] || unset REPLAY_FIXTURE
        export EXECUTION_MODE=Replay REPLAY_URL=$replay_url LOG_EVENTS=false
        unset BENCHMARK_OUTPUT BENCHMARK_EXPECTED_FRAMES BENCHMARK_WARMUP_FRAMES BENCHMARK_DIAGNOSTIC CORRECTNESS_TRACE MEASURE_ALLOCATIONS
        if [[ $entry == rust ]]; then cmd=("$rust_app"); else cmd=("$dotnet" "$csharp_app"); fi
      fi ;;
    list) [[ $entry == simulator ]] || az_fail 'Use run-simulator.sh list'; cmd=("$dotnet" "$simulator" list) ;;
    benchmark|smoke|compare)
      [[ $mode != compare || $entry == simulator ]] || az_fail 'Use run-simulator.sh compare'
      [[ $entry != simulator || $mode == compare || $mode == smoke ]] || az_fail 'Choose compare, or use a language benchmark script'
      # Reports are intentionally readable across local user sessions.
      # mktemp always creates a private directory, regardless of umask.
      if (( ! dry )); then umask 022; fi
      local action=run
      [[ $mode != compare ]] || action=compare
      cmd=("$dotnet" "$simulator" benchmark "$action" --recording "$recording")
      if [[ $entry == rust || $mode == compare ]]; then cmd+=(--rust-app "$rust_app"); fi
      if [[ $entry != rust ]]; then cmd+=(--csharp-app "$csharp_app"); fi
      [[ $entry != rust ]] || cmd+=(--language rust)
      if ((all)); then cmd+=(--all); else cmd+=(--max-messages "$limit"); fi
      if ((smoke)); then cmd+=(--smoke); fi
      if [[ -n $speed ]]; then cmd+=(--speed "$speed"); elif [[ -n $duration || $smoke == 0 ]]; then cmd+=(--target-duration-seconds "${duration:-60}"); fi
      [[ -z $fixture ]] || cmd+=(--fixture "$fixture")
      [[ -z $app_cpus ]] || cmd+=(--app-cpus "$app_cpus")
      if [[ -z $output ]]; then
        if ((dry)); then output="$CSHARP_ROOT/benchmark-results/NEW-$entry-$mode";
        else
          mkdir -p "$CSHARP_ROOT/benchmark-results"
          output=$(mktemp -d "$CSHARP_ROOT/benchmark-results/$(date -u +%Y%m%dT%H%M%SZ)-$entry-$mode-XXXXXX")
          chmod a+rx "$output"
        fi
      fi
      cmd+=(--out "$output" "${extra[@]}") ;;
    *) az_fail "Unknown mode $mode" ;;
  esac
  if [[ -n $app_cpus || -n $sim_cpus ]]; then
    [[ -n $app_cpus && -n $sim_cpus ]] || az_fail 'Set both APP_CPUS and SIMULATOR_CPUS (or the corresponding flags)'
    python3 "$CSHARP_ROOT/scripts/environment.py" affinity "$app_cpus" "$sim_cpus"
    local cpus=$sim_cpus
    [[ $mode != replay || $entry == simulator ]] || cpus=$app_cpus
    [[ $mode == test ]] || cmd=(taskset --cpu-list "$cpus" "${cmd[@]}")
  fi
  if ((dry)); then printf '%q ' "${cmd[@]}"; printf '\n'; return; fi
  if [[ $mode != test ]]; then
    if [[ $mode != replay || $entry == simulator ]]; then [[ -f $simulator ]] || az_fail 'Build the C# solution first'; fi
    if [[ $entry == rust || $mode == compare ]]; then [[ -x $rust_app ]] || az_fail "Rust Release executable not found at $rust_app. Run $RUST_ROOT/scripts/run-rust.sh build"; fi
    if [[ $entry == csharp || $mode == compare ]]; then [[ -f $csharp_app ]] || az_fail 'Build the C# Release application first'; fi
  fi
  [[ -z $output ]] || echo "Results: $output"
  # exec gives the process the terminal signals directly; the coordinator owns and
  # kills its child application on cancellation. There is no background shell job.
  exec "${cmd[@]}"
}

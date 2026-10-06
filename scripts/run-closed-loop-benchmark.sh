#!/usr/bin/env bash
set -euo pipefail
closed_loop_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
# Preserve an explicitly supplied connection; the installed local DB currently uses .2.
closed_loop_pg_explicit=${PG_CONNECTION+x}
source "$closed_loop_root/scripts/benchmark-common.sh"
if [[ -z $closed_loop_pg_explicit ]]; then
  export PG_CONNECTION="${PG_CONNECTION/Host=172.17.0.3/Host=172.17.0.2}"
fi
exec python3 "$closed_loop_root/scripts/closed-loop-benchmark.py" "$@"

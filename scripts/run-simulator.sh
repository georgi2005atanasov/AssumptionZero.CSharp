#!/usr/bin/env bash
set -euo pipefail
script_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
export CSHARP_ROOT="${CSHARP_ROOT:-$script_root}"
if [[ ! -f "$CSHARP_ROOT/scripts/benchmark-common.sh" ]]; then
  export CSHARP_ROOT=$script_root
fi
source "$CSHARP_ROOT/scripts/benchmark-common.sh"
az_main simulator "$@"

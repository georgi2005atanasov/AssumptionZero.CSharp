#!/usr/bin/env bash
set -euo pipefail
export CSHARP_ROOT="${CSHARP_ROOT:-$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)}"
source "$CSHARP_ROOT/scripts/benchmark-common.sh"
az_main csharp "$@"

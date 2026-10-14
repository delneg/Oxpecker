#!/bin/bash
# Usage: bench/timing.sh <runs> <BDN filter...>  -> prints Mean per benchmark for each run
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_ROOT=$HOME/.dotnet-oxpecker PATH=$HOME/.dotnet-oxpecker:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
out=$(dotnet build OxpMicro/OxpMicro.fsproj -c Release 2>&1) || { echo "$out" | grep " error " | head; exit 1; }
RUNS=$1; shift
for r in $(seq 1 $RUNS); do
  dotnet OxpMicro/bin/Release/net10.0/OxpMicro.dll --filter "*A0Warmup*" "$@" 2>&1 | grep -E "^\| [A-Z]" | grep -v -E "Method|A0Warmup" | awk -F'|' '{gsub(/ /,"",$2); gsub(/ /,"",$3); printf "%s=%s ", $2, $3}'
  echo
done
rm -rf BenchmarkDotNet.Artifacts

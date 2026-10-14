#!/bin/bash
# Quick experiment run: Kestrel alloc (all CPUs) + in-process exact alloc. Prints compact lines.
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_ROOT=$HOME/.dotnet-oxpecker PATH=$HOME/.dotnet-oxpecker:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
./autoresearch.sh all ${1:-2} 2>/dev/null | grep -E "METRIC alloc" | sed 's/METRIC //' | tr '\n' ' '; echo
out=$(dotnet build bench/OxpMicro/OxpMicro.fsproj -c Release 2>&1) || { echo "$out" | grep " error " | head; exit 1; }
dotnet bench/OxpMicro/bin/Release/net10.0/OxpMicro.dll --alloc | awk '{split($3,a,"="); printf "%s=%s ", $2, a[2]} END {print ""}'

#!/bin/bash
# Builds the Kestrel bench server and prints METRIC lines (allocation is deterministic, +-2 B/req).
# Usage: ./autoresearch.sh [cpu: all|1] [seconds per endpoint] [endpoints...]
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_ROOT=$HOME/.dotnet-oxpecker PATH=$HOME/.dotnet-oxpecker:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
CPU=${1:-all}; DUR=${2:-3}; [ $# -ge 2 ] && shift 2 || shift $#
out=$(dotnet build bench/OxpBench/OxpBench.fsproj -c Release 2>&1) || { echo "$out" | grep " error " | sort -u | head -20; exit 1; }
OUT=$(bench/measure.sh "$CPU" "$DUR" "$@")
echo "$OUT" >&2
echo "$OUT" | awk '{ep=$1; for(i=3;i<=NF;i++){split($i,kv,"="); if(kv[1]=="alloc_per_req"){print "METRIC alloc_" ep "=" kv[2]; t+=kv[2]} if(kv[1]=="cpu_us_per_req") print "METRIC cpuus_" ep "=" kv[2]; if(kv[1]=="rps") print "METRIC rps_" ep "=" kv[2]}} END {print "METRIC alloc_total=" t}'

#!/bin/bash
# Correctness gate: library builds, all runnable test suites pass, public API unchanged.
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_ROOT=$HOME/.dotnet-oxpecker PATH=$HOME/.dotnet-oxpecker:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
for p in src/Oxpecker/Oxpecker.fsproj src/Oxpecker.ViewEngine/Oxpecker.ViewEngine.fsproj src/Oxpecker.OpenApi/Oxpecker.OpenApi.fsproj \
         src/Oxpecker.Htmx/Oxpecker.Htmx.fsproj src/Oxpecker.Alpine/Oxpecker.Alpine.fsproj examples/Basic/Basic.fsproj; do
  out=$(dotnet build "$p" -c Release 2>&1) || { echo "$out" | grep -E " error " | sort -u | head -20; echo "BUILD FAIL $p"; exit 1; }
done
for t in Oxpecker.Tests Oxpecker.ViewEngine.Tests Oxpecker.Htmx.Tests Oxpecker.Alpine.Tests; do
  out=$(dotnet test tests/$t/$t.fsproj -c Release 2>&1) || { echo "$out" | grep -v NETSDK1057 | tail -40; echo "TEST FAIL $t"; exit 1; }
  echo "$out" | grep -E "Passed!|Failed!" | sed 's/^ *//'
done
out=$(dotnet build bench/ApiDump/ApiDump.fsproj -c Release 2>&1) || { echo "$out" | grep " error " | head; exit 1; }
dotnet bench/ApiDump/bin/Release/net10.0/ApiDump.dll > /tmp/oxp-api-now.txt
diff bench/api-baseline.txt /tmp/oxp-api-now.txt && echo "PUBLIC API UNCHANGED"

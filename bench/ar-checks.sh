#!/bin/bash
# Runs ON wsl26. Correctness gate: src + examples build, runnable test suites pass, fantomas check on src,
# public API dump identical to bench/api-baseline.txt. Exit 0 = pass.
set -euo pipefail
# all work stays on CPUs 0-11 (12-23 belong to another agent)
[ -z "${OXP_PINNED:-}" ] && exec env OXP_PINNED=1 taskset -c 0-11 bash "$0" "$@"
cd "$(dirname "$0")/.."
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
tfm() { grep -o '<TargetFramework>[^<]*' "$1" | sed 's/.*>//'; }
for p in src/Oxpecker/Oxpecker.fsproj src/Oxpecker.ViewEngine/Oxpecker.ViewEngine.fsproj src/Oxpecker.OpenApi/Oxpecker.OpenApi.fsproj \
         src/Oxpecker.Htmx/Oxpecker.Htmx.fsproj src/Oxpecker.Alpine/Oxpecker.Alpine.fsproj \
         examples/Basic/Basic.fsproj examples/Empty/Empty.fsproj examples/ContactApp/ContactApp.fsproj examples/WeatherApp/WeatherApp.fsproj examples/TodoList/TodoList.fsproj; do
  out=$(dotnet build "$p" -c Release 2>&1) || { echo "$out" | grep -E " error " | sort -u | head -20; echo "BUILD FAIL $p"; exit 1; }
done
for t in Oxpecker.Tests Oxpecker.ViewEngine.Tests Oxpecker.Htmx.Tests Oxpecker.Alpine.Tests Oxpecker.OpenApi.Tests; do
  out=$(dotnet test tests/$t/$t.fsproj -c Release 2>&1) || { echo "$out" | grep -v NETSDK1057 | tail -40; echo "TEST FAIL $t"; exit 1; }
  echo "$out" | grep -E "Passed!|Failed!|total:|succeeded:" | sed 's/^ *//' | head -3
done
dotnet tool restore >/dev/null
DOTNET_ROLL_FORWARD=Major dotnet fantomas --check src/Oxpecker src/Oxpecker.ViewEngine || { echo "FANTOMAS FAIL (run: dotnet fantomas src/Oxpecker src/Oxpecker.ViewEngine)"; exit 1; }
echo "FANTOMAS OK"
out=$(dotnet build bench/ApiDump/ApiDump.fsproj -c Release 2>&1) || { echo "$out" | grep " error " | head; exit 1; }
dotnet bench/ApiDump/bin/Release/$(tfm bench/ApiDump/ApiDump.fsproj)/ApiDump.dll > /tmp/oxp-api-now.txt
diff bench/api-baseline.txt /tmp/oxp-api-now.txt && echo "PUBLIC API UNCHANGED"

#!/bin/bash
# Runs ON wsl26 (Ryzen 9 7900X3D, 12 cores / 24 threads, WSL2). Prints METRIC lines on stdout, details on stderr.
# Everything stays on CPUs 0-11 (12-23 belong to another agent's load tests). SMT pairs are 0+1, 2+3, ...
# Kestrel keep-alive, 16 endpoints.
#   cpu1: DOTNET_PROCESSOR_COUNT=1, workstation GC, server on cpu 2, wrk 1 thread 16 conns on cpus 8-11.
#   cpu4: server GC, server on cpus 0,2,4,6 (4 physical cores), wrk 4 threads 64 conns on cpus 8-11.
# In-process (OxpMicro): exact allocated bytes per call (cpu 10) and median ns per call (cpus 10,11: one core + its SMT sibling
# so the background JIT does not share the measured thread's core).
# Usage: bench/ar-bench.sh [seconds per endpoint, default 3]
set -euo pipefail
[ -z "${OXP_PINNED:-}" ] && exec env OXP_PINNED=1 taskset -c 0-11 bash "$0" "$@"
cd "$(dirname "$0")/.."
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
DUR=${1:-3}
EPS="plaintext json json-users user ctx headers composed html subroute bind-query bind-form bind-json nocache precond subroutef html-dynamic"
tfm() { grep -o '<TargetFramework>[^<]*' "$1" | sed 's/.*>//'; }
for p in bench/OxpBench/OxpBench.fsproj bench/OxpMicro/OxpMicro.fsproj; do
  out=$(taskset -c 0-11 dotnet build "$p" -c Release 2>&1) || { echo "$out" | grep -E " error " | sort -u | head -20 >&2; echo "BUILD FAIL $p" >&2; exit 1; }
done
BDLL=bench/OxpBench/bin/Release/$(tfm bench/OxpBench/OxpBench.fsproj)/OxpBench.dll
MDLL=bench/OxpMicro/bin/Release/$(tfm bench/OxpMicro/OxpMicro.fsproj)/OxpMicro.dll

MA=$(DOTNET_gcServer=0 taskset -c 10 dotnet "$MDLL" --alloc)
MT=$(DOTNET_gcServer=0 taskset -c 10,11 dotnet "$MDLL" --time)
echo "$MA" >&2; echo "$MT" >&2

url() { case $1 in
  plaintext) echo /plaintext;; json) echo /json;; json-users) echo /json-users;;
  user) echo /user/don/42;; ctx) echo /userctx/don/42;; headers) echo "/headers?q=1";;
  composed) echo /composed;; html) echo /html;; subroute) echo /api/users;;
  bind-query) echo "/bind-query?Name=don&Age=42";; nocache) echo /nocache;; subroutef) echo /org/acme/info;; html-dynamic) echo /html-dynamic;; precond) echo /precond;; bind-form) echo /bind-form;; bind-json) echo /bind-json;; esac; }
lua() { case $1 in bind-form) echo bench/post-form.lua;; bind-json) echo bench/post-json.lua;; *) echo bench/get.lua;; esac; }
ms() { awk -v v="$1" 'BEGIN { if (v ~ /us$/) { sub(/us/,"",v); print v/1000 } else if (v ~ /ms$/) { sub(/ms/,"",v); print v+0 } else { sub(/s/,"",v); print v*1000 } }'; }

kestrel() { # mode
  local MODE=$1 PORT ENVS SCPU WCPU THREADS CONNS
  if [ "$MODE" = cpu1 ]; then PORT=47101; ENVS=(DOTNET_PROCESSOR_COUNT=1 DOTNET_gcServer=0); SCPU=2; WCPU=8-11; THREADS=1; CONNS=16
  else PORT=47104; ENVS=(DOTNET_gcServer=1); SCPU=0,2,4,6; WCPU=8-11; THREADS=4; CONNS=64; fi
  env "${ENVS[@]}" BENCH_PORT=$PORT taskset -c $SCPU dotnet "$BDLL" >/tmp/oxpbench-$MODE.log 2>&1 &
  local PID=$!
  for i in $(seq 1 100); do curl -s -o /dev/null "http://127.0.0.1:$PORT/__stats" && break; kill -0 $PID 2>/dev/null || { echo "SERVER_DIED $MODE" >&2; tail -n 3 /tmp/oxpbench-$MODE.log >&2; exit 1; }; sleep 0.2; done
  for ep in $EPS; do # global warm-up: every endpoint 1 s, so tiering of shared code is done before any measurement
    taskset -c $WCPU wrk -t$THREADS -c$CONNS -d1s -s "$(lua $ep)" "http://127.0.0.1:$PORT$(url $ep)" >/dev/null
  done
  for ep in $EPS; do
    local U="http://127.0.0.1:$PORT$(url $ep)" C
    case $ep in
      bind-form) C=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/x-www-form-urlencoded' --data 'Name=don&Age=42' "$U");;
      bind-json) C=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' --data '{"name":"don","age":42}' "$U");;
      *) C=$(curl -s -o /dev/null -w '%{http_code}' -H 'X-Test: abc' "$U");;
    esac
    [ "$C" = 200 ] || { echo "ENDPOINT_FAIL $MODE $ep $C" >&2; kill $PID; exit 1; }
    taskset -c $WCPU wrk -t$THREADS -c$CONNS -d1s -s "$(lua $ep)" "$U" >/dev/null
    local S1 S2 OUT
    S1=$(curl -s "http://127.0.0.1:$PORT/__stats")
    OUT=$(taskset -c $WCPU wrk -t$THREADS -c$CONNS -d${DUR}s --latency -s "$(lua $ep)" "$U")
    S2=$(curl -s "http://127.0.0.1:$PORT/__stats")
    local REQS RPS P99
    REQS=$(echo "$OUT" | awk '/requests in/ {print $1}')
    RPS=$(echo "$OUT" | awk '/Requests\/sec/ {print $2}')
    P99=$(ms "$(echo "$OUT" | awk '$1=="99%" {print $2}')")
    echo "$S1 $S2 $REQS" | awk -v ep=$ep -v rps=$RPS -v p99=$P99 -v m=$MODE '{printf "K %s %s alloc=%.0f rps=%.0f p99_ms=%s\n", m, ep, ($7-$1)/$13, rps, p99}'
  done
  kill $PID; wait $PID 2>/dev/null || true
}
K1=$(kestrel cpu1); echo "$K1" >&2
K4=$(kestrel cpu4); echo "$K4" >&2

# METRIC lines
echo "$K1" | awk '{split($4,a,"="); split($5,r,"="); split($6,p,"="); print "METRIC alloc_" $3 "=" a[2]; print "METRIC rps1_" $3 "=" r[2]; t+=a[2]; lr+=log(r[2]); n++; if (p[2]>mp) mp=p[2]}
  END {print "METRIC alloc_total=" t; printf "METRIC rps1_geomean=%.0f\nMETRIC p99_1_max_ms=%s\n", exp(lr/n), mp}'
echo "$K4" | awk '{split($4,a,"="); split($5,r,"="); split($6,p,"="); print "METRIC rps4_" $3 "=" r[2]; t+=a[2]; lr+=log(r[2]); n++; lp+=log(p[2])}
  END {printf "METRIC alloc4_total=%d\nMETRIC rps4_geomean=%.0f\nMETRIC p99_4_geomean_ms=%.3f\n", t, exp(lr/n), exp(lp/n)}'
echo "$MA" | awk '{split($3,a,"="); t+=a[2]} END {printf "METRIC micro_alloc_total=%.1f\n", t}'
echo "$MT" | awk '{split($3,a,"="); print "METRIC ns_" $2 "=" a[2]; t+=a[2]} END {printf "METRIC ns_total=%.1f\n", t}'

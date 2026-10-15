#!/bin/bash
# Interleaved in-process timing, incumbent commit (A) vs working tree (B), both built and run on wsl26.
# Usage: ./ab.sh <incumbent-commit> [rounds, default 3]
# stdout: one line per endpoint "AB <ep> A=<median ns> B=<median ns> d=<B/A-1 %> spreadA=<max-min %>", then AB total.
set -euo pipefail
cd "$(dirname "$0")"
BASE=${1:?incumbent commit}; R=${2:-3}
EX=(--exclude .git --exclude bin --exclude obj --exclude BenchmarkDotNet.Artifacts --exclude node_modules --exclude /experiments --exclude /autoresearch.jsonl)
rsync -a --delete "${EX[@]}" ./ wsl26:oxp-1015/
T=$(mktemp -d /tmp/oxp-ab-src.XXXXXX); git archive "$BASE" | tar -x -C "$T"
rsync -a --delete "${EX[@]}" "$T"/ wsl26:oxp-ab/; rm -rf "$T"
ssh -T -o BatchMode=yes wsl26 "bash -lc 'set -e; export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  for d in oxp-ab oxp-1015; do (cd ~/\$d && dotnet build bench/OxpMicro/OxpMicro.fsproj -c Release >/tmp/ab-build-\$d.log 2>&1) || { grep \" error \" /tmp/ab-build-\$d.log | sort -u | head; exit 1; }; done
  tfm=\$(grep -o \"<TargetFramework>[^<]*\" ~/oxp-1015/bench/OxpMicro/OxpMicro.fsproj | sed \"s/.*>//\")
  tfa=\$(grep -o \"<TargetFramework>[^<]*\" ~/oxp-ab/bench/OxpMicro/OxpMicro.fsproj | sed \"s/.*>//\")
  for i in \$(seq 1 $R); do
    DOTNET_gcServer=0 taskset -c 10,11 dotnet ~/oxp-ab/bench/OxpMicro/bin/Release/\$tfa/OxpMicro.dll --time | sed \"s/^/A /\"
    DOTNET_gcServer=0 taskset -c 10,11 dotnet ~/oxp-1015/bench/OxpMicro/bin/Release/\$tfm/OxpMicro.dll --time | sed \"s/^/B /\"
  done'" | python3 -I -c '
import sys, statistics as st, collections
d = collections.defaultdict(lambda: {"A": [], "B": []}); order = []
for line in sys.stdin:
    p = line.split()
    if len(p) == 4 and p[1] == "MICRO":
        if p[2] not in d: order.append(p[2])
        d[p[2]][p[0]].append(float(p[3].split("=")[1]))
ta = tb = 0.0
for ep in order:
    a, b = st.median(d[ep]["A"]), st.median(d[ep]["B"]); ta += a; tb += b
    sa = (max(d[ep]["A"]) - min(d[ep]["A"])) / a * 100; sb = (max(d[ep]["B"]) - min(d[ep]["B"])) / b * 100
    print(f"AB {ep} A={a:.1f} B={b:.1f} d={(b/a-1)*100:+.1f}% spreadA={sa:.1f}% spreadB={sb:.1f}%")
print(f"AB total A={ta:.1f} B={tb:.1f} d={(tb/ta-1)*100:+.1f}%")
'

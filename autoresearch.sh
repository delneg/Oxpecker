#!/bin/bash
# Runs on the Mac: syncs this worktree to wsl26:~/oxp-1015 and runs the benchmark there.
# stdout: METRIC lines only. stderr: build errors and per-endpoint details.
set -euo pipefail
cd "$(dirname "$0")"
ssh -o ConnectTimeout=15 -o BatchMode=yes wsl26 true 2>/dev/null || { ssh -o BatchMode=yes win 'wsl -d Ubuntu-26.04 -- true' >/dev/null 2>&1 || true; sleep 10; }
rsync -a --delete --exclude .git --exclude bin --exclude obj --exclude BenchmarkDotNet.Artifacts --exclude node_modules \
  --exclude /experiments --exclude /autoresearch.jsonl ./ wsl26:oxp-1015/
ssh -T -o BatchMode=yes wsl26 "bash -lc 'cd ~/oxp-1015 && bash bench/ar-bench.sh ${1:-3}'"

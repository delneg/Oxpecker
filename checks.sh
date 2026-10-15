#!/bin/bash
# Runs on the Mac: sync to wsl26 and run bench/ar-checks.sh there.
set -euo pipefail
cd "$(dirname "$0")"
rsync -a --delete --exclude .git --exclude bin --exclude obj --exclude BenchmarkDotNet.Artifacts --exclude node_modules \
  --exclude /experiments --exclude /autoresearch.jsonl ./ wsl26:oxp-1015/
ssh -T -o BatchMode=yes wsl26 "bash -lc 'cd ~/oxp-1015 && bash bench/ar-checks.sh'"

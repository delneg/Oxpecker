#!/bin/bash
# Usage: bench/keep.sh <NN-name> <commit message> [base branch, default 369cda2]
# Commits src/ changes on the current (stack) branch, then cherry-picks that commit onto exp/<NN-name> cut from base.
set -euo pipefail
cd "$(dirname "$0")/.."
NAME=$1; MSG=$2; BASE=${3:-369cda2}
git add src
git commit -q -m "$MSG"
H=$(git rev-parse --short HEAD)
git branch "exp/$NAME" "$BASE"
rm -rf .wt && git worktree add -q .wt "exp/$NAME"
(cd .wt && git cherry-pick -x "$H" >/dev/null) || { (cd .wt && git cherry-pick --abort); git worktree remove --force .wt; echo "CHERRY-PICK CONFLICT"; exit 1; }
EH=$(cd .wt && git rev-parse --short HEAD)
git worktree remove --force .wt
echo "stack=$H exp/$NAME=$EH"

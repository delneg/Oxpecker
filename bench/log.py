#!/usr/bin/env python3
# Usage: bench/log.py <id> <status> <idea> <files> <metrics> [commit]
import json, sys, datetime
id_, status, idea, files, metrics = sys.argv[1:6]
commit = sys.argv[6] if len(sys.argv) > 6 else ""
rec = {"id": int(id_), "time": datetime.datetime.now().isoformat(timespec="seconds"), "idea": idea,
       "files": files.split(","), "metrics": metrics, "status": status, "commit": commit}
open(__file__.rsplit("/", 2)[0] + "/autoresearch.jsonl", "a").write(json.dumps(rec) + "\n")

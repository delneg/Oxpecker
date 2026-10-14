# Autoresearch: Oxpecker hot-path allocations and speed

## Objective
Make Oxpecker (F# web framework on ASP.NET Core) allocate less per request and run faster on typical
endpoints, with small, separate, PR-sized changes. No public API change, no behaviour change, no new
runtime dependencies. Inspired by "don't create them": pooled buffers, stackalloc, cached objects,
~0 new objects per request so the GC stays idle on a 1 vCPU box.

## Metrics
- **Primary**: `alloc_total` = sum over 9 endpoints of server-side allocated bytes per request under
  real Kestrel + wrk keep-alive (B/req, lower is better). Deterministic to about +-2 B/req per endpoint.
- **Per endpoint**: `alloc_<ep>` (plaintext, json, json-users, user=routef, ctx=routef with ctx,
  headers=header+query read, composed=>=> chain, html=htmlView, subroute).
- **Secondary**: in-process `MICRO <ep> alloc_per_call` (exact, `bench/OxpMicro --alloc`) and
  BenchmarkDotNet mean time per call (`bench/OxpMicro`, in-process, full ASP.NET pipeline on a reused
  feature set). Kestrel rps and server CPU us/req are printed but are NOT trustworthy on this busy Mac
  (load avg ~60, wrk-bound at ~30k rps, thread-pool spinning dominates CPU time).

## How to Run
- `./autoresearch.sh [all|1] [seconds]` builds `bench/OxpBench` and prints METRIC lines
  (`1` = `DOTNET_PROCESSOR_COUNT=1`, 1 wrk thread, 8 connections; `all` = server GC, 2 threads, 32 conns).
- `dotnet bench/OxpMicro/bin/Release/net10.0/OxpMicro.dll --alloc` exact allocations per call.
- `dotnet bench/OxpMicro/bin/Release/net10.0/OxpMicro.dll --filter '*'` BDN timings (~3 min).
- `./autoresearch.checks.sh` builds src + examples/Basic, runs Oxpecker, ViewEngine, Htmx, Alpine tests,
  and diffs a reflection dump of the public API against `bench/api-baseline.txt`.
- Env: `DOTNET_ROOT=~/.dotnet-oxpecker` (SDK 11.0.100-rc.1 per global.json, runtime 10.0.13; the
  apphost pack 10.0.12 was copied into ~/.dotnet-oxpecker/packs by hand). If NuGet says a package
  version doesn't exist, `dotnet restore --no-http-cache` (stale HTTP cache).

## Files in Scope
- `src/Oxpecker/*.fs`: Routing.fs (routef invoke), HttpContextExtensions.fs (WriteText/WriteBytes/
  WriteHtmlView/TryGet*), Serializers.fs (JSON), Core.fs (>=> compose), Handlers.fs.
- `src/Oxpecker.ViewEngine/*.fs`: Render.fs / Builder.fs (HTML rendering used by htmlView).

## Off Limits
- Public signatures (checked by API dump), tests (only run), examples, Solid/Fable projects.
- Pushing, PRs, anything outside this directory.

## Constraints
- All runnable tests pass; public API dump identical; no new runtime deps in src.
- Each kept change = one commit on its own `exp/NN-name` branch cut from upstream `369cda2`,
  also stacked on `autoresearch/perf-2026-10-14`.
- Known unrelated env breakage: `tests/Oxpecker.OpenApi.Tests` and `examples/MCP` failed restore at
  baseline (stale NuGet HTTP cache), so they are not in the checks.

## What's Been Tried
(see autoresearch.jsonl; summary updated as we go)

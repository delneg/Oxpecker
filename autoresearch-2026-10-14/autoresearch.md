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
Full log: autoresearch.jsonl. 16 experiments: 15 kept (exp/01..15), 1 discarded (16).
- 01 routef GetRouteData()->Request.RouteValues (-40 B); 02 fixed-arity Invoke for no-ctx routef (-40 B, on 01)
- 03 WriteText pooled buffer (-32..40 B per text response; 2000-char WriteText 321->205 ns)
- 04 WriteHtmlView without StreamWriter (-5.3 KB/html); 06 pooled buffer for html (-240 B, on 05+04);
  12 per-thread strict encoder (-48 B, on 04); 13 per-thread spare PooledBufferStream (-40 B json/html, on 06)
- 05 JSON PooledBufferStream instead of RecyclableMemoryStream (-240 B/json, Json 1.1-1.4 us -> 0.7 us in-process)
- 07 bindQuery: HttpRequest.Query is QueryCollectionInternal -> pooled dict instead of new Dictionary (-216 B)
- 08 BindJson returns completed inner task (-72 B)
- 09 noResponseCaching precomputed header string (-297 B); 10 preconditions without closures (-170..191 B)
- 11 Compose sync fast path (CPU: Compose4 48->27 ns, headers chain 242->209 ns; end-to-end within noise)
- 14 routef '%' check before case-insensitive Replace (CPU: ~5 ns per string segment)
- 15 exceptionMiddleware sync fast path (CPU: 32-42 ns -> 4 ns per request)
- DISCARDED 16 WriteText single-pass encoding for short strings (~4 ns, not worth the branching)
- Combined (Kestrel, 16 endpoints): alloc_total 21341 -> 8204 B/req (all CPUs), 21289 -> 8169 (1 core).
- Insights: in-process harness floor 320 B/call; Kestrel floor ~76 B/req. End-to-end timing noise 5-10% on this
  busy Mac, direct BDN ~2%; Kestrel rps/CPU numbers are not trustworthy here (wrk-bound, load avg 15-60).
  A try/with inside a composed closure changes JIT results (jsonl 11). Remaining allocations are mostly
  ASP.NET (route values, query/form parsing, STJ internals), user code, or API-mandated (Option, Task<'T>).

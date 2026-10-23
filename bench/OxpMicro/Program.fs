/// In-process benchmark: runs the full ASP.NET Core pipeline (routing middleware + Oxpecker endpoint)
/// on a reused feature set with a Pipe-backed response body, so only framework work is measured.
module OxpMicro.Program

open System
open System.IO
open System.IO.Pipelines
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Configs
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Running
open BenchmarkDotNet.Toolchains.InProcess.Emit
open Oxpecker

type PipeBodyFeature(pipe: Pipe) =
    let stream = pipe.Writer.AsStream(true)
    interface IHttpResponseBodyFeature with
        member _.Stream = stream
        member _.Writer = pipe.Writer
        member _.DisableBuffering() = ()
        member _.StartAsync(_) = Task.CompletedTask
        member _.SendFileAsync(_, _, _, _) = Task.CompletedTask
        member _.CompleteAsync() = Task.CompletedTask

type Harness() =
    let builder = WebApplication.CreateBuilder()
    do builder.Logging.ClearProviders() |> ignore
    do builder.Services.AddRouting().AddOxpecker() |> ignore
    let app = builder.Build()
    do app.UseRouting() |> ignore
    do app.UseOxpecker(OxpBench.Endpoints.endpoints) |> ignore
    let pipeline = (app :> IApplicationBuilder).Build()
    let pipe =
        Pipe(PipeOptions(pauseWriterThreshold = 0L, resumeWriterThreshold = 0L, useSynchronizationContext = false))
    let features = FeatureCollection()
    let req = HttpRequestFeature()
    let resp = HttpResponseFeature()
    let reqHeaders = HeaderDictionary()
    let respHeaders = HeaderDictionary()
    do
        reqHeaders["X-Test"] <- "abc"
        reqHeaders["If-None-Match"] <- "\"v0\""
        req.Headers <- reqHeaders
        resp.Headers <- respHeaders
        features.Set<IHttpRequestFeature>(req)
        features.Set<IHttpResponseFeature>(resp)
        features.Set<IHttpResponseBodyFeature>(PipeBodyFeature(pipe))
        features.Set<IServiceProvidersFeature>(ServiceProvidersFeature(RequestServices = app.Services))
    member this.Run(path: string, query: string) =
        this.Run("GET", path, query, null, null)
    member _.Run(method: string, path: string, query: string, contentType: string, body: byte array) =
        req.Method <- method
        req.Path <- path
        req.QueryString <- query
        if isNull body then
            reqHeaders.Remove("Content-Type") |> ignore
            reqHeaders.ContentLength <- Nullable()
            req.Body <- Stream.Null
        else
            reqHeaders["Content-Type"] <- contentType
            reqHeaders.ContentLength <- int64 body.Length
            req.Body <- new MemoryStream(body, false)
        resp.StatusCode <- 200
        respHeaders.Clear()
        features.Set<IEndpointFeature>(null)
        features.Set<IRouteValuesFeature>(null)
        let ctx = DefaultHttpContext(features)
        let t = pipeline.Invoke(ctx)
        if not t.IsCompletedSuccessfully then
            t.GetAwaiter().GetResult()
        if resp.StatusCode <> 200 then
            failwithf "status %d for %s" resp.StatusCode path
        pipe.Writer.FlushAsync().GetAwaiter().GetResult() |> ignore
        match pipe.Reader.TryRead() with
        | true, r -> pipe.Reader.AdvanceTo(r.Buffer.End)
        | _ -> ()

let formBody = Text.Encoding.UTF8.GetBytes "Name=don&Age=42"
let jsonBody = Text.Encoding.UTF8.GetBytes """{"name":"don","age":42}"""

let cases = [
    "plaintext", "/plaintext", ""
    "json", "/json", ""
    "json-users", "/json-users", ""
    "user", "/user/don/42", ""
    "ctx", "/userctx/don/42", ""
    "headers", "/headers", "?q=1"
    "composed", "/composed", ""
    "html", "/html", ""
    "subroute", "/api/users", ""
    "precond", "/precond", ""
    "html-dynamic", "/html-dynamic", ""
    "nocache", "/nocache", ""
    "bind-query", "/bind-query", "?Name=don&Age=42"
    "bind-form", "/bind-form", ""
    "bind-json", "/bind-json", ""
    "subroutef", "/org/acme/info", ""
]

let run (h: Harness) name (p: string) (q: string) =
    match name with
    | "bind-form" -> h.Run("POST", p, q, "application/x-www-form-urlencoded", formBody)
    | "bind-json" -> h.Run("POST", p, q, "application/json", jsonBody)
    | _ -> h.Run(p, q)

[<MemoryDiagnoser>]
type Endpoints() =
    let h = Harness()
    [<Benchmark>]
    member _.A0Warmup() =
        for _ in 1..4 do
            h.Run("/plaintext", "")
    [<Benchmark>]
    member _.Plaintext() = h.Run("/plaintext", "")
    [<Benchmark>]
    member _.Json() = h.Run("/json", "")
    [<Benchmark>]
    member _.JsonUsers() = h.Run("/json-users", "")
    [<Benchmark>]
    member _.Routef() = h.Run("/user/don/42", "")
    [<Benchmark>]
    member _.RoutefCtx() = h.Run("/userctx/don/42", "")
    [<Benchmark>]
    member _.HeaderQuery() = h.Run("/headers", "?q=1")
    [<Benchmark>]
    member _.Composed() = h.Run("/composed", "")
    [<Benchmark>]
    member _.Html() = h.Run("/html", "")
    [<Benchmark>]
    member _.SubRoute() = h.Run("/api/users", "")
    [<Benchmark>]
    member _.Precond() = h.Run("/precond", "")
    [<Benchmark>]
    member _.HtmlDynamic() = h.Run("/html-dynamic", "")
    [<Benchmark>]
    member _.NoCache() = h.Run("/nocache", "")
    [<Benchmark>]
    member _.BindQuery() =
        h.Run("/bind-query", "?Name=don&Age=42")
    [<Benchmark>]
    member _.BindForm() = run h "bind-form" "/bind-form" ""
    [<Benchmark>]
    member _.BindJson() = run h "bind-json" "/bind-json" ""
    [<Benchmark>]
    member _.SubRoutef() = h.Run("/org/acme/info", "")

type AllocListener() =
    inherit Diagnostics.Tracing.EventListener()
    member val Counts = Collections.Concurrent.ConcurrentDictionary<string, int64>()
    override this.OnEventSourceCreated(src) =
        if src.Name = "Microsoft-Windows-DotNETRuntime" then
            this.EnableEvents(
                src,
                Diagnostics.Tracing.EventLevel.Verbose,
                LanguagePrimitives.EnumOfValue<int64, Diagnostics.Tracing.EventKeywords> 1L
            )
    override this.OnEventWritten(e) =
        if e.EventName <> null && e.EventName.StartsWith "GCAllocationTick" then
            let i = e.PayloadNames.IndexOf "TypeName"
            let name = if i >= 0 then string e.Payload[i] else "?"
            this.Counts.AddOrUpdate(name, 1L, (fun _ v -> v + 1L)) |> ignore

[<EntryPoint>]
let main args =
    let only = args |> Array.skip(min 1 args.Length) |> Set.ofArray
    let selected = cases |> List.filter(fun (n, _, _) -> only.IsEmpty || only.Contains n)
    if args.Length > 1 && args[0] = "--profile" then
        // sampled allocation types (one sample per ~100 KB allocated) for one endpoint
        let h = Harness()
        let name, p, q = cases |> List.find(fun (n, _, _) -> n = args[1])
        for _ in 1..20000 do
            run h name p q
        use l = new AllocListener()
        for _ in 1..2000000 do
            run h name p q
        for KeyValue(k, v) in l.Counts |> Seq.sortByDescending(fun kv -> kv.Value) |> Seq.truncate 25 do
            printfn "%8d %s" v k
        0
    elif args.Length > 0 && args[0] = "--time" then
        // warm every endpoint 1.5 s, then per endpoint 0.3 s re-warm + 9 rounds of 40k calls, median ns/call
        let h = Harness()
        let warm ms name p q =
            let sw = Diagnostics.Stopwatch.StartNew()
            while sw.ElapsedMilliseconds < ms do
                for _ in 1..1000 do
                    run h name p q
        for name, p, q in selected do
            warm 1500L name p q
        for name, p, q in selected do
            warm 300L name p q
            let n = 40000
            let rounds =
                [|
                    for _ in 1..9 do
                        let t0 = Diagnostics.Stopwatch.GetTimestamp()
                        for _ in 1..n do
                            run h name p q
                        let t1 = Diagnostics.Stopwatch.GetTimestamp()
                        float(t1 - t0) * 1e9 / float Diagnostics.Stopwatch.Frequency / float n
                |]
                |> Array.sort
            printfn "MICRO %s ns_per_call=%.1f" name rounds[4]
        0
    elif args.Length > 0 && args[0] = "--alloc" then
        // deterministic allocated bytes per call, no BDN
        let h = Harness()
        for name, p, q in selected do
            for _ in 1..20000 do
                run h name p q
            let n = 20000
            let a0 = GC.GetAllocatedBytesForCurrentThread()
            for _ in 1..n do
                run h name p q
            let a1 = GC.GetAllocatedBytesForCurrentThread()
            printfn "MICRO %s alloc_per_call=%.1f" name (float(a1 - a0) / float n)
        0
    else
        let job =
            Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithWarmupCount(3)
                .WithIterationCount(15)
                .WithInvocationCount(16384)
                .WithUnrollFactor(16)
        let config = DefaultConfig.Instance.AddJob(job).WithOptions(ConfigOptions.DisableOptimizationsValidator)
        BenchmarkSwitcher.FromAssembly(typeof<Endpoints>.Assembly).Run(args, config)
        |> ignore
        0

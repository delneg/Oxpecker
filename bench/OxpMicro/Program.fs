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
    let pipe = Pipe(PipeOptions(pauseWriterThreshold = 0L, resumeWriterThreshold = 0L, useSynchronizationContext = false))
    let features = FeatureCollection()
    let req = HttpRequestFeature()
    let resp = HttpResponseFeature()
    let reqHeaders = HeaderDictionary()
    let respHeaders = HeaderDictionary()
    do
        reqHeaders["X-Test"] <- "abc"
        req.Headers <- reqHeaders
        resp.Headers <- respHeaders
        features.Set<IHttpRequestFeature>(req)
        features.Set<IHttpResponseFeature>(resp)
        features.Set<IHttpResponseBodyFeature>(PipeBodyFeature(pipe))
        features.Set<IServiceProvidersFeature>(ServiceProvidersFeature(RequestServices = app.Services))
    member _.Run(path: string, query: string) =
        req.Method <- "GET"
        req.Path <- path
        req.QueryString <- query
        resp.StatusCode <- 200
        respHeaders.Clear()
        features.Set<IEndpointFeature>(null)
        features.Set<IRouteValuesFeature>(null)
        let ctx = DefaultHttpContext(features)
        let t = pipeline.Invoke(ctx)
        if not t.IsCompletedSuccessfully then t.GetAwaiter().GetResult()
        if resp.StatusCode <> 200 then failwithf "status %d for %s" resp.StatusCode path
        pipe.Writer.FlushAsync().GetAwaiter().GetResult() |> ignore
        match pipe.Reader.TryRead() with
        | true, r -> pipe.Reader.AdvanceTo(r.Buffer.End)
        | _ -> ()

[<MemoryDiagnoser>]
type Endpoints() =
    let h = Harness()
    [<Benchmark>] member _.Plaintext() = h.Run("/plaintext", "")
    [<Benchmark>] member _.Json() = h.Run("/json", "")
    [<Benchmark>] member _.JsonUsers() = h.Run("/json-users", "")
    [<Benchmark>] member _.Routef() = h.Run("/user/don/42", "")
    [<Benchmark>] member _.RoutefCtx() = h.Run("/userctx/don/42", "")
    [<Benchmark>] member _.HeaderQuery() = h.Run("/headers", "?q=1")
    [<Benchmark>] member _.Composed() = h.Run("/composed", "")
    [<Benchmark>] member _.Html() = h.Run("/html", "")
    [<Benchmark>] member _.SubRoute() = h.Run("/api/users", "")

type AllocListener() =
    inherit Diagnostics.Tracing.EventListener()
    member val Counts = Collections.Concurrent.ConcurrentDictionary<string, int64>()
    override this.OnEventSourceCreated(src) =
        if src.Name = "Microsoft-Windows-DotNETRuntime" then
            this.EnableEvents(src, Diagnostics.Tracing.EventLevel.Verbose, LanguagePrimitives.EnumOfValue<int64, Diagnostics.Tracing.EventKeywords> 1L)
    override this.OnEventWritten(e) =
        if e.EventName <> null && e.EventName.StartsWith "GCAllocationTick" then
            let i = e.PayloadNames.IndexOf "TypeName"
            let name = if i >= 0 then string e.Payload[i] else "?"
            this.Counts.AddOrUpdate(name, 1L, fun _ v -> v + 1L) |> ignore

[<EntryPoint>]
let main args =
    if args.Length > 1 && args[0] = "--profile" then
        // sampled allocation types (one sample per ~100 KB allocated) for one endpoint
        let h = Harness()
        let path, query = match args[1] with "headers" -> "/headers", "?q=1" | "user" -> "/user/don/42", "" | "ctx" -> "/userctx/don/42", "" | "subroute" -> "/api/users", "" | p -> "/" + p, ""
        for _ in 1..20000 do h.Run(path, query)
        use l = new AllocListener()
        for _ in 1..2000000 do h.Run(path, query)
        for KeyValue(k, v) in l.Counts |> Seq.sortByDescending (fun kv -> kv.Value) |> Seq.truncate 25 do printfn "%8d %s" v k
        exit 0
    if args.Length > 0 && args[0] = "--alloc" then
        // deterministic allocated bytes per call, no BDN
        let h = Harness()
        let cases = [ "plaintext","/plaintext",""; "json","/json",""; "json-users","/json-users",""; "user","/user/don/42","";
                      "ctx","/userctx/don/42",""; "headers","/headers","?q=1"; "composed","/composed",""; "html","/html",""; "subroute","/api/users","" ]
        for name, p, q in cases do
            for _ in 1..20000 do h.Run(p, q)
            let n = 20000
            let a0 = GC.GetAllocatedBytesForCurrentThread()
            for _ in 1..n do h.Run(p, q)
            let a1 = GC.GetAllocatedBytesForCurrentThread()
            printfn "MICRO %s alloc_per_call=%.1f" name (float (a1 - a0) / float n)
        0
    else
        let job = Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithWarmupCount(20).WithIterationCount(25).WithInvocationCount(16384).WithUnrollFactor(16)
        let config = DefaultConfig.Instance.AddJob(job).WithOptions(ConfigOptions.DisableOptimizationsValidator)
        BenchmarkSwitcher.FromAssembly(typeof<Endpoints>.Assembly).Run(args, config) |> ignore
        0

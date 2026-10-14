namespace Oxpecker

open System.Runtime.ExceptionServices
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

[<AutoOpen>]
module CoreTypes =

    /// Fsharp-friendly alias for RequestDelegate
    type EndpointHandler = HttpContext -> Task

    /// Endpoint middleware is analogue to ASP.NET Core Middleware, but for Endpoint level.
    type EndpointMiddleware = EndpointHandler -> EndpointHandler

module CoreInternal =

    let inline compose_opImpl (_: ^OpImpl) left right =
        ((^OpImpl or ^left): (static member Compose: ^left * ^right -> ^right) (left, right))
    // A synchronous throw becomes a failed task (canceled for OperationCanceledException), as it did inside the task builder
    let private failedTask (ex: exn) : Task =
        task { ExceptionDispatchInfo.Throw ex }

    type Composition =
        static member Compose(handler1: EndpointHandler, handler2: EndpointHandler) : EndpointHandler =
            fun (ctx: HttpContext) ->
                match ctx.Response.HasStarted with
                | true -> Task.CompletedTask
                | false ->
                    let task1 =
                        try
                            handler1 ctx
                        with ex ->
                            failedTask ex
                    // most handlers complete synchronously: skip the task state machine then
                    if task1.IsCompletedSuccessfully then
                        match ctx.Response.HasStarted with
                        | true -> Task.CompletedTask
                        | false ->
                            let task2 =
                                try
                                    handler2 ctx
                                with ex ->
                                    failedTask ex
                            if task2.IsCompletedSuccessfully then
                                task2
                            else
                                // awaited by the task builder as before, e.g. a faulted cancellation becomes canceled
                                task { return! task2 }
                    else
                        task {
                            do! task1
                            match ctx.Response.HasStarted with
                            | true -> ()
                            | false -> return! handler2 ctx
                        }

        static member Compose(middleware: EndpointMiddleware, handler: EndpointHandler) : EndpointHandler =
            fun (ctx: HttpContext) ->
                match ctx.Response.HasStarted with
                | true -> Task.CompletedTask
                | false -> middleware handler ctx

        static member Compose(middleware1: EndpointMiddleware, middleware2: EndpointMiddleware) : EndpointMiddleware =
            fun (next: EndpointHandler) ->
                let resultMiddleware = next |> middleware2 |> middleware1
                fun (ctx: HttpContext) ->
                    match ctx.Response.HasStarted with
                    | true -> next ctx
                    | false -> resultMiddleware ctx


[<AutoOpen>]
module Core =

    open CoreInternal

    /// <summary>
    /// Combines two <see cref="EndpointHandler"/> or two <see cref="EndpointMiddleware"/> functions into one. Also can combine middleware with handler (but not vise versa)
    /// </summary>
    let inline (>=>) left right =
        compose_opImpl Unchecked.defaultof<Composition> left right

    /// Same as >=>, but with additional argument
    let inline (>>=>) left right x = left >=> (right x)

    /// Same as >=>, but with two arguments
    let inline (>>=>+) left right x y = left >=> (right x y)

    /// Same as >=>, but with three arguments
    let inline (>>=>++) left right x y z = left >=> (right x y z)

    /// Implicit conversion from IResult to EndpointHandler
    let inline (~%) (result: IResult) : EndpointHandler =
        fun (ctx: HttpContext) -> ctx.Write result

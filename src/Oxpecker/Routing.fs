namespace Oxpecker

open System
open System.Collections.Concurrent
open System.Reflection
open System.Runtime.CompilerServices
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.AspNetCore.Builder
open Microsoft.FSharp.Core
open Oxpecker

[<AutoOpen>]
module RoutingTypes =

    type HttpVerb =
        | GET
        | POST
        | PUT
        | PATCH
        | QUERY
        | DELETE
        | HEAD
        | OPTIONS
        | TRACE
        | CONNECT

        override this.ToString() =
            match this with
            | GET -> "GET"
            | POST -> "POST"
            | PUT -> "PUT"
            | PATCH -> "PATCH"
            | QUERY -> "QUERY"
            | DELETE -> "DELETE"
            | HEAD -> "HEAD"
            | OPTIONS -> "OPTIONS"
            | TRACE -> "TRACE"
            | CONNECT -> "CONNECT"

    type HttpVerbs =
        | Verbs of HttpVerb seq
        | Any

    type RouteTemplate = string
    type ConfigureEndpoint = IEndpointConventionBuilder -> IEndpointConventionBuilder
    type Endpoint =
        | SimpleEndpoint of HttpVerbs * RouteTemplate * EndpointHandler * ConfigureEndpoint
        | NestedEndpoint of RouteTemplate * Endpoint seq * ConfigureEndpoint
        | MultiEndpoint of Endpoint seq * ConfigureEndpoint

module RouteTemplateBuilder =

    // Kestrel has made the weird decision to
    // partially decode a route argument, which
    // means that a given route argument would get
    // entirely URL decoded except for '%2F' (/).
    // Hence, decoding %2F must happen separately as
    // part of the string parsing function.
    //
    // For more information please check:
    // https://github.com/aspnet/Mvc/issues/4599

    let inline parse (c: char) (modifier: string option) (s: string) : obj =
        try
            match c with
            | 's' ->
                // the vectorized char search is much cheaper than a case-insensitive substring search
                if s.Contains('%') then
                    s.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)
                else
                    s
            | 'i' -> int s |> boxv
            | 'b' -> bool.Parse s |> boxv
            | 'c' -> char s[0] |> boxv
            | 'd' -> int64 s |> boxv
            | 'f' -> float s |> boxv
            | 'u' -> uint64 s |> boxv
            | 'O' ->
                match modifier with
                | Some "guid" -> Guid.Parse s |> boxv
                | _ -> s
            | _ -> s
        with :? FormatException as ex ->
            raise
            <| RouteParseException($"Url segment value '%s{s}' has invalid format", ex)

    let placeholderPattern = Regex("\{(\*{0,2})%([sibcdfuO])(:[^}]+)?\}")
    // This function should convert to route template and mappings
    // "api/{%s}/{%i}" -> ("api/{x}/{y}", [("x", 's', None); ("y", 'i', None)])
    // "api/{%O:guid}/{%s}" -> ("api/{x:guid}/{y}", [("x", 'O', Some "guid"); ("y", 's', None)])
    let convertToRouteTemplate (pathValue: string) (parameters: ParameterInfo[]) =
        let mutable index = 0
        let mappings = ResizeArray()

        let placeholderEvaluator =
            MatchEvaluator(fun m ->
                let slug = m.Groups[1].Value
                let vtype = m.Groups[2].Value[0] // Second capture group is the variable type s, i, or O
                let formatSpecifier = if m.Groups[3].Success then m.Groups[3].Value else ""
                let paramName = parameters[index].Name |> string
                index <- index + 1 // Increment index for next use
                mappings.Add(
                    paramName,
                    vtype,
                    if formatSpecifier = "" then
                        None
                    else
                        Some <| formatSpecifier.TrimStart(':')
                )
                $"{{%s{slug}%s{paramName}%s{formatSpecifier}}}" // Construct the new placeholder
            )

        let newRoute = placeholderPattern.Replace(pathValue, placeholderEvaluator)
        (newRoute, mappings.ToArray())

module RoutingInternal =
    type AddFilter =
        static member Compose(filter: EndpointHandler, endpoint: Endpoint) =
            match endpoint with
            | SimpleEndpoint(verb, template, handler, configure) ->
                SimpleEndpoint(verb, template, filter >=> handler, configure)
            | NestedEndpoint(template, endpoints, configure) ->
                NestedEndpoint(template, Seq.map (fun e -> AddFilter.Compose(filter, e)) endpoints, configure)
            | MultiEndpoint(endpoints, configure) ->
                MultiEndpoint(Seq.map (fun e -> AddFilter.Compose(filter, e)) endpoints, configure)

        static member Compose(filterMiddleware: EndpointMiddleware, endpoint: Endpoint) =
            match endpoint with
            | SimpleEndpoint(verb, template, handler, configure) ->
                SimpleEndpoint(verb, template, filterMiddleware >=> handler, configure)
            | NestedEndpoint(template, endpoints, configure) ->
                NestedEndpoint(template, Seq.map (fun e -> AddFilter.Compose(filterMiddleware, e)) endpoints, configure)
            | MultiEndpoint(endpoints, configure) ->
                MultiEndpoint(Seq.map (fun e -> AddFilter.Compose(filterMiddleware, e)) endpoints, configure)

    let private getArgByIndex
        (routeValues: RouteValueDictionary)
        (mappings: (string * char * Option<_>) array)
        (index: int)
        =
        let placeholderName, formatChar, modifier = mappings[index]
        let routeValue = routeValues[placeholderName] |> string
        RouteTemplateBuilder.parse formatChar modifier routeValue

    let private invokeHandler<'T>
        (ctx: HttpContext)
        (invoker: MethodInvoker)
        (handler: 'T)
        (mappings: (string * char * Option<_>) array)
        (ctxInParameterList: bool)
        =
        // Request.RouteValues is what GetRouteData() wraps, without allocating a RouteData per request
        let routeValues = ctx.Request.RouteValues
        if ctxInParameterList then
            match mappings.Length with
            | 0 -> invoker.Invoke(handler, ctx)
            | 1 ->
                let arg = getArgByIndex routeValues mappings 0
                invoker.Invoke(handler, arg, ctx)
            | 2 ->
                let arg1 = getArgByIndex routeValues mappings 0
                let arg2 = getArgByIndex routeValues mappings 1
                invoker.Invoke(handler, arg1, arg2, ctx)
            | 3 ->
                let arg1 = getArgByIndex routeValues mappings 0
                let arg2 = getArgByIndex routeValues mappings 1
                let arg3 = getArgByIndex routeValues mappings 2
                invoker.Invoke(handler, arg1, arg2, arg3, ctx)
            | _ ->
                invoker.Invoke(
                    handler,
                    Span [|
                        for placeholderName, formatChar, modifier in mappings do
                            let routeValue = routeValues[placeholderName] |> string
                            RouteTemplateBuilder.parse formatChar modifier routeValue
                        ctx
                    |]
                )
            |> nonNull
            :?> Task
        else
            match mappings.Length with
            | 1 -> invoker.Invoke(handler, getArgByIndex routeValues mappings 0)
            | 2 ->
                let arg1 = getArgByIndex routeValues mappings 0
                let arg2 = getArgByIndex routeValues mappings 1
                invoker.Invoke(handler, arg1, arg2)
            | 3 ->
                let arg1 = getArgByIndex routeValues mappings 0
                let arg2 = getArgByIndex routeValues mappings 1
                let arg3 = getArgByIndex routeValues mappings 2
                invoker.Invoke(handler, arg1, arg2, arg3)
            | _ ->
                invoker.Invoke(
                    handler,
                    Span [|
                        for placeholderName, formatChar, modifier in mappings do
                            let routeValue = routeValues[placeholderName] |> string
                            RouteTemplateBuilder.parse formatChar modifier routeValue
                    |]
                )
            |> nonNull
            :?> FSharpFunc<HttpContext, Task>
            <| ctx

    /// routef handlers called with typed arguments: segments parsed exactly like RouteTemplateBuilder.parse but
    /// without boxing, and the curried handler applied with InvokeFast instead of reflection
    [<AbstractClass; Sealed>]
    type private TypedRoutef =
        static member inline private Invalid (s: string) (ex: exn) : 'a =
            raise
            <| RouteParseException($"Url segment value '%s{s}' has invalid format", ex)

        /// null when parse would not give an 'A for this format (the handler then keeps the reflection path)
        static member Parser<'A>(formatChar: char, modifier: string option) : Func<string, 'A> | null =
            let typed (parser: Func<string, 'P>) : Func<string, 'A> | null = unbox(box parser)
            let t = typeof<'A>
            if t = typeof<obj> then
                typed(Func<string, obj>(fun s -> RouteTemplateBuilder.parse formatChar modifier s))
            else
                match formatChar, modifier with
                | 'i', _ when t = typeof<int> ->
                    typed(
                        Func<string, int>(fun s ->
                            try
                                int s
                            with :? FormatException as ex ->
                                TypedRoutef.Invalid s ex)
                    )
                | 'b', _ when t = typeof<bool> ->
                    typed(
                        Func<string, bool>(fun s ->
                            try
                                bool.Parse s
                            with :? FormatException as ex ->
                                TypedRoutef.Invalid s ex)
                    )
                | 'c', _ when t = typeof<char> ->
                    typed(
                        Func<string, char>(fun s ->
                            try
                                char s[0]
                            with :? FormatException as ex ->
                                TypedRoutef.Invalid s ex)
                    )
                | 'd', _ when t = typeof<int64> ->
                    typed(
                        Func<string, int64>(fun s ->
                            try
                                int64 s
                            with :? FormatException as ex ->
                                TypedRoutef.Invalid s ex)
                    )
                | 'f', _ when t = typeof<float> ->
                    typed(
                        Func<string, float>(fun s ->
                            try
                                float s
                            with :? FormatException as ex ->
                                TypedRoutef.Invalid s ex)
                    )
                | 'u', _ when t = typeof<uint64> ->
                    typed(
                        Func<string, uint64>(fun s ->
                            try
                                uint64 s
                            with :? FormatException as ex ->
                                TypedRoutef.Invalid s ex)
                    )
                | 'O', Some "guid" when t = typeof<Guid> ->
                    typed(
                        Func<string, Guid>(fun s ->
                            try
                                Guid.Parse s
                            with :? FormatException as ex ->
                                TypedRoutef.Invalid s ex)
                    )
                | ('i' | 'b' | 'c' | 'd' | 'f' | 'u'), _
                | 'O', Some "guid" -> null
                | _ when t = typeof<string> ->
                    typed(Func<string, string>(fun s -> RouteTemplateBuilder.parse formatChar modifier s :?> string))
                | _ -> null

        static member private Value (ctx: HttpContext) (name: string) = ctx.Request.RouteValues[name] |> string

        static member Make1<'A>(handler: obj, mappings: (string * char * string option) array) =
            let name1, c1, m1 = mappings[0]
            match TypedRoutef.Parser<'A>(c1, m1) with
            | null -> null
            | p1 ->
                let f: 'A -> HttpContext -> Task = unbox handler
                Func<HttpContext, Task>(fun ctx ->
                    let routeValues = ctx.Request.RouteValues
                    f (p1.Invoke(routeValues[name1] |> string)) ctx)

        static member Make2<'A, 'B>(handler: obj, mappings: (string * char * string option) array) =
            let name1, c1, m1 = mappings[0]
            let name2, c2, m2 = mappings[1]
            match TypedRoutef.Parser<'A>(c1, m1), TypedRoutef.Parser<'B>(c2, m2) with
            | null, _
            | _, null -> null
            | p1, p2 ->
                let f: 'A -> 'B -> HttpContext -> Task = unbox handler
                Func<HttpContext, Task>(fun ctx ->
                    let routeValues = ctx.Request.RouteValues
                    let a = p1.Invoke(routeValues[name1] |> string)
                    let b = p2.Invoke(routeValues[name2] |> string)
                    f a b ctx)

        static member Make3<'A, 'B, 'C>(handler: obj, mappings: (string * char * string option) array) =
            let name1, c1, m1 = mappings[0]
            let name2, c2, m2 = mappings[1]
            let name3, c3, m3 = mappings[2]
            match TypedRoutef.Parser<'A>(c1, m1), TypedRoutef.Parser<'B>(c2, m2), TypedRoutef.Parser<'C>(c3, m3) with
            | null, _, _
            | _, null, _
            | _, _, null -> null
            | p1, p2, p3 ->
                let f: 'A -> 'B -> 'C -> HttpContext -> Task = unbox handler
                Func<HttpContext, Task>(fun ctx ->
                    let routeValues = ctx.Request.RouteValues
                    let a = p1.Invoke(routeValues[name1] |> string)
                    let b = p2.Invoke(routeValues[name2] |> string)
                    let c = p3.Invoke(routeValues[name3] |> string)
                    f a b c ctx)

        /// A typed request delegate for a handler of type handlerType with 1 to 3 route arguments, else null
        static member TryCreate
            (handlerType: Type, handler: obj | null, mappings: (string * char * string option) array)
            : Func<HttpContext, Task> | null =
            let rec domains (t: Type) n =
                if n = 0 then
                    if t = typeof<EndpointHandler> then Some [] else None
                elif Reflection.FSharpType.IsFunction t then
                    let domain, range = Reflection.FSharpType.GetFunctionElements t
                    domains range (n - 1) |> Option.map(fun rest -> domain :: rest)
                else
                    None
            if mappings.Length < 1 || mappings.Length > 3 then
                null
            else
                match domains handlerType mappings.Length with
                | Some argTypes ->
                    let make =
                        typeof<TypedRoutef>
                            .GetMethod(
                                $"Make{mappings.Length}",
                                BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static
                            )
                        |> nonNull
                    match make.MakeGenericMethod(Array.ofList argTypes).Invoke(null, [| handler; box mappings |]) with
                    | :? Func<HttpContext, Task> as typed -> typed
                    | _ -> null
                | None -> null

    [<NoEquality; NoComparison>]
    type private RoutefInfo = {
        Template: RouteTemplate
        Mappings: (string * char * string option) array
        Parameters: ParameterInfo array
        Invoker: MethodInvoker
    }

    // Holds only data derived from the handler type and the format string, never handler instances,
    // so routef endpoints rebuilt on every request (inside subRoutef) don't repeat reflection
    let private routefInfoCache = ConcurrentDictionary<struct (Type * string), RoutefInfo>()

    let private createRoutefInfo =
        Func<struct (Type * string), RoutefInfo>(fun (struct (handlerType, path)) ->
            let handlerMethod = handlerType.GetMethods()[0]
            let parameters = handlerMethod.GetParameters()
            if parameters.Length < RouteTemplateBuilder.placeholderPattern.Count(path) then
                failwith $"Handler has fewer parameters than route placeholders: %s{path}"
            let template, mappings = RouteTemplateBuilder.convertToRouteTemplate path parameters
            {
                Template = template
                Mappings = mappings
                Parameters = parameters
                Invoker = MethodInvoker.Create(handlerMethod)
            })

    let private getRoutefInfo (path: string) (handlerType: Type) =
        routefInfoCache.GetOrAdd(struct (handlerType, path), createRoutefInfo)

    let routefInner (path: PrintfFormat<'T, unit, unit, EndpointHandler>) (handler: 'T) =
        let info = getRoutefInfo path.Value (handler.GetType())
        let ctxInParameterList =
            if info.Parameters.Length = info.Mappings.Length + 1 then
                true
            elif info.Parameters.Length = info.Mappings.Length then
                false
            else
                failwith <| "Unsupported routef handler: " + path.Value
        let typed = TypedRoutef.TryCreate(typeof<'T>, box handler, info.Mappings)
        let requestDelegate =
            fun (ctx: HttpContext) ->
                match typed with
                | null -> invokeHandler<'T> ctx info.Invoker handler info.Mappings ctxInParameterList
                | typed -> typed.Invoke ctx

        info.Template, info.Mappings, requestDelegate

    // Argument for the startup call of a subRoutef factory. Real path segments are never empty, hence non-empty string
    let private placeholderArg (parameter: ParameterInfo) =
        let parameterType = parameter.ParameterType
        if parameterType = typeof<string> || parameterType = typeof<obj> then
            box "placeholder"
        elif parameterType.IsValueType then
            Activator.CreateInstance parameterType
        else
            null

    let private invokeFactory (ctx: HttpContext) (info: RoutefInfo) (factory: obj) =
        let routeValues = ctx.Request.RouteValues
        let mappings = info.Mappings
        match mappings.Length with
        | 1 -> info.Invoker.Invoke(factory, getArgByIndex routeValues mappings 0)
        | 2 ->
            let arg1 = getArgByIndex routeValues mappings 0
            let arg2 = getArgByIndex routeValues mappings 1
            info.Invoker.Invoke(factory, arg1, arg2)
        | 3 ->
            let arg1 = getArgByIndex routeValues mappings 0
            let arg2 = getArgByIndex routeValues mappings 1
            let arg3 = getArgByIndex routeValues mappings 2
            info.Invoker.Invoke(factory, arg1, arg2, arg3)
        | _ -> info.Invoker.Invoke(factory, Span(Array.init mappings.Length (getArgByIndex routeValues mappings)))
        |> nonNull
        :?> Endpoint seq

    // Structure, templates and HTTP verbs of the endpoints returned by subRoutef factory at startup
    [<NoEquality; NoComparison>]
    type private EndpointShape =
        | SimpleShape of HttpVerbs * RouteTemplate
        | NestedShape of RouteTemplate * EndpointShape array
        | MultiShape of EndpointShape array

    // Verbs are usually the same instance, since GET, POST, etc. are partially applied functions
    let private isSameVerbs (verbs: HttpVerbs) (expectedVerbs: HttpVerbs) =
        match verbs, expectedVerbs with
        | Any, Any -> true
        | Verbs verbs, Verbs expectedVerbs ->
            obj.ReferenceEquals(verbs, expectedVerbs)
            || Linq.Enumerable.SequenceEqual(verbs, expectedVerbs)
        | _ -> false

    // Snapshot of the endpoints tree, so that it can be traversed several times even if built from single-use sequences
    let rec private materializeEndpoints (endpoints: Endpoint seq) : Endpoint seq =
        endpoints
        |> Seq.map(function
            | NestedEndpoint(template, children, configure) ->
                NestedEndpoint(template, materializeEndpoints children, configure)
            | MultiEndpoint(children, configure) -> MultiEndpoint(materializeEndpoints children, configure)
            | endpoint -> endpoint)
        |> Seq.toArray
        :> Endpoint seq

    let rec private getShapes (endpoints: Endpoint seq) =
        endpoints
        |> Seq.map(function
            | SimpleEndpoint(verbs, template, _, _) -> SimpleShape(verbs, template)
            | NestedEndpoint(template, children, _) -> NestedShape(template, getShapes children)
            | MultiEndpoint(children, _) -> MultiShape(getShapes children))
        |> Seq.toArray

    // Checks that all endpoints have the same structure and templates as at startup and returns the handler at indexPath
    // (depth is -1 for endpoints outside of indexPath, which are only checked)
    let rec private tryFindHandler
        (shapes: EndpointShape array)
        (indexPath: int array)
        (depth: int)
        (endpoints: Endpoint seq)
        : struct (bool * EndpointHandler voption) =
        let mutable index = 0
        let mutable isValid = true
        let mutable handler = ValueNone
        // lists and arrays (what factories and HTTP verb functions return) are walked without an enumerator
        match endpoints with
        | :? (Endpoint list) as list ->
            let mutable rest = list
            while isValid && not rest.IsEmpty do
                isValid <- checkEndpoint shapes indexPath depth index rest.Head &handler
                index <- index + 1
                rest <- rest.Tail
        | :? (Endpoint array) as array ->
            while isValid && index < array.Length do
                isValid <- checkEndpoint shapes indexPath depth index array[index] &handler
                index <- index + 1
        | _ ->
            use enumerator = endpoints.GetEnumerator()
            while isValid && enumerator.MoveNext() do
                isValid <- checkEndpoint shapes indexPath depth index enumerator.Current &handler
                index <- index + 1
        struct (isValid && index = shapes.Length, handler)

    and private checkEndpoint
        (shapes: EndpointShape array)
        (indexPath: int array)
        (depth: int)
        (index: int)
        (endpoint: Endpoint)
        (handler: byref<EndpointHandler voption>)
        : bool =
        if index < shapes.Length then
            let isOnPath = depth >= 0 && index = indexPath[depth]
            let childDepth = if isOnPath then depth + 1 else -1
            match endpoint, shapes[index] with
            | SimpleEndpoint(verbs, template, endpointHandler, _), SimpleShape(expectedVerbs, expected) when
                template = expected && isSameVerbs verbs expectedVerbs
                ->
                if isOnPath then
                    handler <- ValueSome endpointHandler
                true
            | NestedEndpoint(template, children, _), NestedShape(expected, childShapes) when template = expected ->
                let struct (isChildValid, childHandler) = tryFindHandler childShapes indexPath childDepth children
                if isOnPath then
                    handler <- childHandler
                isChildValid
            | MultiEndpoint(children, _), MultiShape childShapes ->
                let struct (isChildValid, childHandler) = tryFindHandler childShapes indexPath childDepth children
                if isOnPath then
                    handler <- childHandler
                isChildValid
            | _ -> false
        else
            false

    // Keeps the structure, templates, verbs and configuration of the endpoints, but replaces handlers with resolvers
    let rec private wrapEndpoints
        (resolve: int array -> EndpointHandler)
        (indexPath: int list)
        (endpoints: Endpoint seq)
        : Endpoint seq =
        endpoints
        |> Seq.mapi(fun index endpoint ->
            let indexPath = index :: indexPath
            match endpoint with
            | SimpleEndpoint(verbs, template, _, configure) ->
                SimpleEndpoint(verbs, template, resolve(indexPath |> List.rev |> List.toArray), configure)
            | NestedEndpoint(template, children, configure) ->
                NestedEndpoint(template, wrapEndpoints resolve indexPath children, configure)
            | MultiEndpoint(children, configure) -> MultiEndpoint(wrapEndpoints resolve indexPath children, configure))
        |> Seq.toArray
        :> Endpoint seq

    let private subRoutefFromFactory (path: string) (factory: obj) =
        let info = getRoutefInfo path (factory.GetType())
        if info.Parameters.Length <> info.Mappings.Length then
            failwith <| "Unsupported subRoutef endpoints factory: " + path
        if info.Template.Contains("{*") then
            failwith <| "Catch-all parameters are not supported in subRoutef: " + path
        let startupEndpoints =
            try
                info.Invoker.Invoke(factory, Span(Array.map placeholderArg info.Parameters))
                |> nonNull
                :?> Endpoint seq
                |> materializeEndpoints
            with ex ->
                raise
                <| InvalidOperationException(
                    $"subRoutef '%s{path}': endpoints factory failed when called with placeholder arguments at startup. It should only construct endpoints.",
                    ex
                )
        let shapes = getShapes startupEndpoints
        let resolve (indexPath: int array) : EndpointHandler =
            fun ctx ->
                match invokeFactory ctx info factory |> tryFindHandler shapes indexPath 0 with
                | struct (true, ValueSome handler) -> handler ctx
                | _ ->
                    raise
                    <| InvalidOperationException(
                        $"subRoutef '%s{path}': endpoints factory returned different endpoints than at startup. Number, order, nesting, templates and HTTP verbs of endpoints must not depend on route values."
                    )
        info.Template, info.Mappings, wrapEndpoints resolve [] startupEndpoints

    let subRoutefInner<'T, 'Endpoints when 'Endpoints :> Endpoint seq>
        (path: PrintfFormat<'T, unit, unit, 'Endpoints>)
        (endpointsFactory: 'T)
        =
        match box endpointsFactory with
        | :? (Endpoint seq) as endpoints -> path.Value, [||], endpoints
        | factory -> subRoutefFromFactory path.Value (nonNull factory)


[<AutoOpen>]
module Routers =
    open CoreInternal
    open RoutingInternal

    let rec applyHttpVerbsToEndpoint (verbs: HttpVerbs) (endpoint: Endpoint) : Endpoint =
        match endpoint with
        | SimpleEndpoint(oldVerbs, routeTemplate, handler, configure) ->
            if oldVerbs = HttpVerbs.Any || oldVerbs = verbs then
                SimpleEndpoint(verbs, routeTemplate, handler, configure)
            else
                failwithf $"Http verbs intersect at '%s{routeTemplate}'"
        | NestedEndpoint(handler, endpoints, configure) ->
            NestedEndpoint(handler, applyHttpVerbsToSeq verbs endpoints, configure)
        | MultiEndpoint(endpoints, configure) -> MultiEndpoint(applyHttpVerbsToSeq verbs endpoints, configure)

    // Lists and arrays are mapped once, into an array, so the endpoints are not rebuilt on every enumeration
    // (subRoutef factories run per request). Other sequences, and collections where verbs intersect, stay lazy,
    // so their errors are thrown when the endpoints are enumerated, as before.
    and private applyHttpVerbsToSeq (verbs: HttpVerbs) (endpoints: Endpoint seq) : Endpoint seq =
        match endpoints with
        | :? (Endpoint list) as list ->
            let result = Array.zeroCreate list.Length
            let mutable rest = list
            let mutable index = 0
            let mutable isValid = true
            while isValid && not rest.IsEmpty do
                match tryApplyHttpVerbs verbs rest.Head with
                | ValueSome endpoint ->
                    result[index] <- endpoint
                    index <- index + 1
                    rest <- rest.Tail
                | ValueNone -> isValid <- false
            if isValid then
                result :> Endpoint seq
            else
                endpoints |> Seq.map(applyHttpVerbsToEndpoint verbs)
        | :? (Endpoint array) as array ->
            let result = Array.zeroCreate array.Length
            let mutable index = 0
            let mutable isValid = true
            while isValid && index < array.Length do
                match tryApplyHttpVerbs verbs array[index] with
                | ValueSome endpoint ->
                    result[index] <- endpoint
                    index <- index + 1
                | ValueNone -> isValid <- false
            if isValid then
                result :> Endpoint seq
            else
                endpoints |> Seq.map(applyHttpVerbsToEndpoint verbs)
        | _ -> endpoints |> Seq.map(applyHttpVerbsToEndpoint verbs)

    // ValueNone when verbs intersect (the lazy path then throws on enumeration)
    and private tryApplyHttpVerbs (verbs: HttpVerbs) (endpoint: Endpoint) : Endpoint voption =
        match endpoint with
        | SimpleEndpoint(oldVerbs, routeTemplate, handler, configure) ->
            if oldVerbs = HttpVerbs.Any || oldVerbs = verbs then
                ValueSome(SimpleEndpoint(verbs, routeTemplate, handler, configure))
            else
                ValueNone
        | NestedEndpoint(template, endpoints, configure) ->
            ValueSome(NestedEndpoint(template, applyHttpVerbsToSeq verbs endpoints, configure))
        | MultiEndpoint(endpoints, configure) ->
            ValueSome(MultiEndpoint(applyHttpVerbsToSeq verbs endpoints, configure))

    let rec applyHttpVerbsToEndpoints (verbs: HttpVerbs) (endpoints: Endpoint seq) : Endpoint =
        MultiEndpoint(applyHttpVerbsToSeq verbs endpoints, id)

    let GET_HEAD: Endpoint seq -> Endpoint = applyHttpVerbsToEndpoints(Verbs [ GET; HEAD ])

    let GET: Endpoint seq -> Endpoint = applyHttpVerbsToEndpoints(Verbs [ GET ])
    let POST: Endpoint seq -> Endpoint = applyHttpVerbsToEndpoints(Verbs [ POST ])
    let PUT: Endpoint seq -> Endpoint = applyHttpVerbsToEndpoints(Verbs [ PUT ])
    let PATCH: Endpoint seq -> Endpoint = applyHttpVerbsToEndpoints(Verbs [ PATCH ])
    let QUERY: Endpoint seq -> Endpoint = applyHttpVerbsToEndpoints(Verbs [ QUERY ])
    let DELETE: Endpoint seq -> Endpoint = applyHttpVerbsToEndpoints(Verbs [ DELETE ])
    let HEAD: Endpoint seq -> Endpoint = applyHttpVerbsToEndpoints(Verbs [ HEAD ])
    let OPTIONS: Endpoint seq -> Endpoint = applyHttpVerbsToEndpoints(Verbs [ OPTIONS ])
    let TRACE: Endpoint seq -> Endpoint = applyHttpVerbsToEndpoints(Verbs [ TRACE ])
    let CONNECT: Endpoint seq -> Endpoint = applyHttpVerbsToEndpoints(Verbs [ CONNECT ])

    let route (path: string) (handler: EndpointHandler) : Endpoint =
        SimpleEndpoint(HttpVerbs.Any, path, handler, id)

    let routef (path: PrintfFormat<'T, unit, unit, EndpointHandler>) (handler: 'T) : Endpoint =
        let template, _, requestDelegate = routefInner path handler
        SimpleEndpoint(HttpVerbs.Any, template, requestDelegate, id)

    let subRoute (path: string) (endpoints: Endpoint seq) : Endpoint = NestedEndpoint(path, endpoints, id)

    let subRoutef (path: PrintfFormat<'T, unit, unit, #seq<Endpoint>>) (endpointsFactory: 'T) : Endpoint =
        let template, _, endpoints = subRoutefInner path endpointsFactory
        NestedEndpoint(template, endpoints, id)

    let routeGroup (endpoints: Endpoint seq) : Endpoint = MultiEndpoint(endpoints, id)

    let rec configureEndpoint (f: ConfigureEndpoint) (endpoint: Endpoint) =
        match endpoint with
        | SimpleEndpoint(verb, template, handler, configure) -> SimpleEndpoint(verb, template, handler, configure >> f)
        | NestedEndpoint(template, endpoints, configure) -> NestedEndpoint(template, endpoints, configure >> f)
        | MultiEndpoint(endpoints, configure) -> MultiEndpoint(endpoints, configure >> f)

    let inline addFilter (filter: 'T) (endpoint: Endpoint) =
        compose_opImpl Unchecked.defaultof<AddFilter> filter endpoint

    let addMetadata (metadata: obj) =
        configureEndpoint _.WithMetadata(metadata)

type EndpointRouteBuilderExtensions() =

    static member private GetConfigureEndpoint(configure: ConfigureEndpoint, addAntiforgery: bool) =
        if addAntiforgery then
            _.WithMetadata(RequireAntiforgeryTokenAttribute()) >> configure
        else
            configure

    static member private GetConfigureEndpoint
        (verbs: HttpVerb seq, configure: ConfigureEndpoint, addAntiforgery: bool)
        =
        if addAntiforgery then
            let canHaveForm =
                verbs
                |> Seq.exists(fun verb -> verb = HttpVerb.POST || verb = HttpVerb.PUT || verb = HttpVerb.PATCH)
            if canHaveForm then
                _.WithMetadata(RequireAntiforgeryTokenAttribute()) >> configure
            else
                configure
        else
            configure

    [<Extension>]
    static member private IsAntiforgeryEnabled(builder: IEndpointRouteBuilder) =
        match builder.ServiceProvider.GetService(typeof<IAntiforgery>) with
        | null -> false
        | _ -> true

    [<Extension>]
    static member private MapSingleEndpoint
        (
            builder: IEndpointRouteBuilder,
            verb: HttpVerbs,
            routeTemplate: RouteTemplate,
            requestDelegate: RequestDelegate,
            configure: ConfigureEndpoint,
            addAntiforgery: bool
        ) =
        match verb with
        | Any ->
            builder.Map(routeTemplate, requestDelegate)
            |> EndpointRouteBuilderExtensions.GetConfigureEndpoint(configure, addAntiforgery)
        | Verbs verbs ->
            builder.MapMethods(routeTemplate, verbs |> Seq.map string, requestDelegate)
            |> EndpointRouteBuilderExtensions.GetConfigureEndpoint(verbs, configure, addAntiforgery)
        |> ignore

    [<Extension>]
    static member private MapNestedEndpoint
        (
            builder: IEndpointRouteBuilder,
            parentTemplate: RouteTemplate,
            endpoints: Endpoint seq,
            parentConfigure: ConfigureEndpoint,
            addAntiforgery: bool
        ) =
        let groupBuilder = builder.MapGroup(parentTemplate)
        let groupConfigure = EndpointRouteBuilderExtensions.GetConfigureEndpoint(parentConfigure, addAntiforgery)
        groupBuilder |> groupConfigure |> ignore
        for endpoint in endpoints do
            match endpoint with
            | SimpleEndpoint(verb, template, handler, configure) ->
                groupBuilder.MapSingleEndpoint(verb, template, handler, configure, addAntiforgery)
            | NestedEndpoint(template, endpoints, configure) ->
                groupBuilder.MapNestedEndpoint(template, endpoints, configure, addAntiforgery)
            | MultiEndpoint(endpoints, configure) -> groupBuilder.MapMultiEndpoint(endpoints, configure, addAntiforgery)

    [<Extension>]
    static member private MapMultiEndpoint
        (
            builder: IEndpointRouteBuilder,
            endpoints: Endpoint seq,
            parentConfigure: ConfigureEndpoint,
            addAntiforgery: bool
        ) =
        builder.MapNestedEndpoint("", endpoints, parentConfigure, addAntiforgery)

    [<Extension>]
    static member MapOxpeckerEndpoint(builder: IEndpointRouteBuilder, endpoint: Endpoint) =
        let addAntiforgery = builder.IsAntiforgeryEnabled()
        match endpoint with
        | SimpleEndpoint(verb, template, handler, configure) ->
            builder.MapSingleEndpoint(verb, template, handler, configure, addAntiforgery)
        | NestedEndpoint(template, endpoints, configure) ->
            builder.MapNestedEndpoint(template, endpoints, configure, addAntiforgery)
        | MultiEndpoint(endpoints, configure) -> builder.MapMultiEndpoint(endpoints, configure, addAntiforgery)

    [<Extension>]
    static member MapOxpeckerEndpoints(builder: IEndpointRouteBuilder, endpoints: Endpoint seq) =
        let addAntiforgery = builder.IsAntiforgeryEnabled()
        for endpoint in endpoints do
            match endpoint with
            | SimpleEndpoint(verb, template, handler, configure) ->
                builder.MapSingleEndpoint(verb, template, handler, configure, addAntiforgery)
            | NestedEndpoint(template, endpoints, configure) ->
                builder.MapNestedEndpoint(template, endpoints, configure, addAntiforgery)
            | MultiEndpoint(endpoints, configure) -> builder.MapMultiEndpoint(endpoints, configure, addAntiforgery)

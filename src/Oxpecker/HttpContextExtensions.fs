namespace Oxpecker

open System
open System.Buffers
open System.Collections.Generic
open System.IO
open System.Runtime.CompilerServices
open System.Runtime.ExceptionServices
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Extensions
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Net.Http.Headers
open Oxpecker.ViewEngine

type internal StrictUtf8Encoder =
    // the encoding a StreamWriter uses by default: UTF-8 without BOM that throws on invalid surrogates
    static member val private Encoding = UTF8Encoding(false, true)
    [<ThreadStatic; DefaultValue>]
    static val mutable private cached: Encoder | null
    /// One encoder per thread instead of one per response; encoding calls no user code, so it is never shared
    static member Get() =
        match StrictUtf8Encoder.cached with
        | null ->
            let encoder = StrictUtf8Encoder.Encoding.GetEncoder()
            StrictUtf8Encoder.cached <- encoder
            encoder
        | encoder ->
            // drops any state left by an encoding that threw
            encoder.Reset()
            encoder

module internal HtmlWriting =
    /// Encodes the builder into the writer with the same bytes and errors as a default StreamWriter,
    /// without allocating StreamWriter's char and byte buffers
    let encodeStrictUtf8 (sb: StringBuilder) (writer: IBufferWriter<byte>) =
        let encoder = StrictUtf8Encoder.Get()
        let mutable bytesUsed = 0L
        let mutable completed = false
        for chunk in sb.GetChunks() do
            encoder.Convert(chunk.Span, writer, false, &bytesUsed, &completed)
        encoder.Convert(ReadOnlySpan<char>.Empty, writer, true, &bytesUsed, &completed)

type RouteParseException(message: string, ex) =
    inherit Exception(message, ex)

type ModelBindException(message: string, ex) =
    inherit Exception(message, ex)

/// Oxpecker's singleton services, read from the application's root provider so that a request which needs only
/// them does not create a request scope. Only a service whose last registration is a singleton is served here
/// (the same instance RequestServices would return); otherwise callers use RequestServices as before.
[<Sealed>]
type internal RootServices(services: IServiceCollection, root: IServiceProvider) =
    static let isSingleton (services: IServiceCollection) (serviceType: Type) =
        // GetService returns the last registration
        let mutable lifetime = ValueNone
        for descriptor in services do
            if descriptor.ServiceType = serviceType && not descriptor.IsKeyedService then
                lifetime <- ValueSome descriptor.Lifetime
        lifetime = ValueSome ServiceLifetime.Singleton
    let jsonIsSingleton = isSingleton services typeof<IJsonSerializer>
    let binderIsSingleton = isSingleton services typeof<IModelBinder>
    let mutable json: IJsonSerializer | null = null
    let mutable binder: IModelBinder | null = null

    member _.JsonSerializer: IJsonSerializer | null =
        match json with
        | null when jsonIsSingleton ->
            match root.GetService(typeof<IJsonSerializer>) with
            | :? IJsonSerializer as s ->
                json <- s
                s
            | _ -> null
        | s -> s

    member _.ModelBinder: IModelBinder | null =
        match binder with
        | null when binderIsSingleton ->
            match root.GetService(typeof<IModelBinder>) with
            | :? IModelBinder as b ->
                binder <- b
                b
            | _ -> null
        | b -> b

    /// The services of the application serving ctx, if ctx has not created its request services yet
    static member TryGet(ctx: HttpContext) : RootServices | null =
        match ctx with
        | :? DefaultHttpContext as defaultContext ->
            match defaultContext.ServiceScopeFactory with
            // Microsoft.Extensions.DependencyInjection's root scope factory is the root provider itself
            | :? IServiceProvider as rootProvider when
                isNull(box(ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IServiceProvidersFeature>()))
                ->
                match rootProvider.GetService(typeof<RootServices>) with
                | :? RootServices as s -> s
                | _ -> null
            | _ -> null
        | _ -> null

type HttpContextExtensions() =

    /// <summary>
    /// Returns the entire request URL in a fully escaped form, which is suitable for use in HTTP headers and other operations.
    /// </summary>
    /// <returns>Returns an instance of `'T`.</returns>
    [<Extension>]
    static member GetRequestUrl(ctx: HttpContext) = ctx.Request.GetEncodedUrl()

    /// <summary>
    /// Tries to get the value from the route values collection and cast it to `'T`.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="key">The name of the HTTP header.</param>
    /// <returns>Returns a <see cref="System.String"/> URL.</returns>
    [<Extension>]
    static member TryGetRouteValue<'T>(ctx: HttpContext, key: string) =
        match ctx.Request.RouteValues.TryGetValue key with
        | true, value -> value :?> 'T |> Some
        | _ -> None

    /// <summary>
    /// Tries to retrieve the <see cref="System.String"/> value of a cookie from the request
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="key">The name of the cookie.</param>
    /// <returns>Returns Some string if the cookie was set, otherwise returns None.</returns>
    [<Extension>]
    static member TryGetCookieValue(ctx: HttpContext, key: string) =
        match ctx.Request.Cookies.TryGetValue key with
        | true, value -> (Unchecked.nonNull value) |> Some
        | _ -> None

    /// <summary>
    /// Tries to get the <see cref="System.String"/> value of a HTTP header from the request.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="key">The name of the HTTP header.</param>
    /// <returns> Returns Some string if the HTTP header was present in the request, otherwise returns None.</returns>
    [<Extension>]
    static member TryGetHeaderValue(ctx: HttpContext, key: string) =
        match ctx.Request.Headers.TryGetValue key with
        | true, value -> value.ToString() |> Some
        | _ -> None

    /// <summary>
    /// Tries to get the string seq value of a HTTP header from the request.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="key">The name of the HTTP header.</param>
    /// <returns> Returns Some string seq if the HTTP header was present in the request, otherwise returns None.</returns>
    [<Extension>]
    static member TryGetHeaderValues(ctx: HttpContext, key: string) =
        match ctx.Request.Headers.TryGetValue key with
        // StringValues is immutable and never holds null entries, so it can be handed out
        // directly as a string seq instead of paying for a Seq.map wrapper
        | true, value -> Some(boxv value :?> string seq)
        | _ -> None

    /// <summary>
    ///  Tries to get the <see cref="System.String"/> value of a query string parameter from the request.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="key">The name of the query string parameter.</param>
    /// <returns>Returns Some string if the parameter was present in the request's query string, otherwise returns None.</returns>
    [<Extension>]
    static member TryGetQueryValue(ctx: HttpContext, key: string) =
        match ctx.Request.Query.TryGetValue key with
        | true, value -> value.ToString() |> Some
        | _ -> None

    /// <summary>
    /// Tries to get the string seq value of a query string parameter from the request.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="key">The name of the query string parameter.</param>
    /// <returns>Returns Some string seq if the parameter was present in the request's query string, otherwise returns None.</returns>
    [<Extension>]
    static member TryGetQueryValues(ctx: HttpContext, key: string) =
        match ctx.Request.Query.TryGetValue key with
        // StringValues is immutable and never holds null entries, so it can be handed out
        // directly as a string seq instead of paying for a Seq.map wrapper
        | true, value -> Some(boxv value :?> string seq)
        | _ -> None

    /// <summary>
    /// Tries to get the <see cref="System.String"/> value of a form parameter from the request.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="key">The name of the query string parameter.</param>
    /// <returns>Returns Some string if the parameter was present in the request's form, otherwise returns None.</returns>
    [<Extension>]
    static member TryGetFormValue(ctx: HttpContext, key: string) =
        match ctx.Request.Form.TryGetValue key with
        | true, value -> value.ToString() |> Some
        | _ -> None

    /// <summary>
    /// Tries to get the string seq value of a form parameter from the request.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="key">The name of the query string parameter.</param>
    /// <returns>Returns Some string seq if the parameter was present in the request's form, otherwise returns None.</returns>
    [<Extension>]
    static member TryGetFormValues(ctx: HttpContext, key: string) =
        match ctx.Request.Form.TryGetValue key with
        // StringValues is immutable and never holds null entries, so it can be handed out
        // directly as a string seq instead of paying for a Seq.map wrapper
        | true, value -> Some(boxv value :?> string seq)
        | _ -> None

    /// <summary>
    /// Gets an instance of `'T` from the request's service container.
    /// </summary>
    /// <returns>Returns an instance of `'T`.</returns>
    [<Extension>]
    static member GetService(ctx: HttpContext) =
        ctx.RequestServices.GetRequiredService<'T>()

    /// <summary>
    /// Gets an instance of <see cref="Microsoft.Extensions.Logging.ILogger{T}" /> from the request's service container.
    ///
    /// The type `'T` should represent the class or module from where the logger gets instantiated.
    /// </summary>
    /// <returns> Returns an instance of <see cref="Microsoft.Extensions.Logging.ILogger{T}" />.</returns>
    [<Extension>]
    static member GetLogger<'T>(ctx: HttpContext) = ctx.GetService<ILogger<'T>>()

    /// <summary>
    /// Gets an instance of <see cref="Microsoft.Extensions.Logging.ILogger" /> from the request's service container.    ///
    /// </summary>
    /// <returns> Returns an instance of <see cref="Microsoft.Extensions.Logging.ILogger" />.</returns>
    [<Extension>]
    static member GetLogger(ctx: HttpContext) = ctx.GetService<ILogger>()

    /// <summary>
    /// Gets an instance of <see cref="Microsoft.Extensions.Logging.ILogger"/> from the request's service container.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="categoryName">The category name for messages produced by this logger.</param>
    /// <returns>Returns an instance of <see cref="Microsoft.Extensions.Logging.ILogger"/>.</returns>
    [<Extension>]
    static member GetLogger(ctx: HttpContext, categoryName: string) =
        let loggerFactory = ctx.GetService<ILoggerFactory>()
        loggerFactory.CreateLogger categoryName

    /// <summary>
    /// Gets an instance of <see cref="Microsoft.AspNetCore.Hosting.IWebHostEnvironment"/> from the request's service container.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <returns>Returns an instance of <see cref="Microsoft.AspNetCore.Hosting.IWebHostEnvironment"/>.</returns>
    [<Extension>]
    static member GetWebHostEnvironment(ctx: HttpContext) = ctx.GetService<IWebHostEnvironment>()

    /// <summary>
    /// Gets an instance of <see cref="Oxpecker.IJsonSerializer"/> from the request's service container.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <returns>Returns an instance of <see cref="Oxpecker.IJsonSerializer"/>.</returns>
    [<Extension>]
    static member GetJsonSerializer(ctx: HttpContext) : IJsonSerializer =
        match RootServices.TryGet ctx with
        | null -> ctx.GetService<IJsonSerializer>()
        | services ->
            match services.JsonSerializer with
            | null -> ctx.GetService<IJsonSerializer>()
            | serializer -> serializer

    /// <summary>
    /// Gets an instance of <see cref="Oxpecker.IModelBinder"/> from the request's service container.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <returns>Returns an instance of <see cref="Oxpecker.IModelBinder"/>.</returns>
    [<Extension>]
    static member GetModelBinder(ctx: HttpContext) : IModelBinder =
        match RootServices.TryGet ctx with
        | null -> ctx.GetService<IModelBinder>()
        | services ->
            match services.ModelBinder with
            | null -> ctx.GetService<IModelBinder>()
            | binder -> binder

    /// <summary>
    /// Gets an instance of <see cref="Microsoft.AspNetCore.Antiforgery.AntiforgeryTokenSet"/> from the request's service container.'
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <returns>Returns an instance of <see cref="Microsoft.AspNetCore.Antiforgery.AntiforgeryTokenSet"/>.</returns>
    [<Extension>]
    static member GetAntiforgeryTokens(ctx: HttpContext) =
        ctx.GetService<IAntiforgery>().GetAndStoreTokens(ctx)

    /// <summary>
    /// Gets an HTML input object of <see cref=" Oxpecker.ViewEngine.Tags.input"/> type, already filled with the Antiforgery data
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <returns>Returns an instance of <see cref=" Oxpecker.ViewEngine.Tags.input"/>.</returns>
    [<Extension>]
    static member GetAntiforgeryInput(ctx: HttpContext) =
        let tokens = ctx.GetAntiforgeryTokens()
        input(type' = "hidden", name = tokens.FormFieldName, value = tokens.RequestToken)

    /// <summary>
    /// Sets the HTTP status code of the response.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="httpStatusCode">The status code to be set in the response. For convenience, you can use the static <see cref="Microsoft.AspNetCore.Http.StatusCodes"/> class for passing in named status codes instead of using pure int values.</param>
    [<Extension>]
    static member SetStatusCode(ctx: HttpContext, httpStatusCode: int) =
        ctx.Response.StatusCode <- httpStatusCode

    /// <summary>
    /// Adds or sets an HTTP header in the response.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="key">The HTTP header name. For convenience, you can use the static <see cref="Microsoft.Net.Http.Headers.HeaderNames"/> class for passing in strongly typed header names instead of using pure `string` values.</param>
    /// <param name="value">The value to be set. Non string values will be converted to a string using the object's ToString() method.</param>
    [<Extension>]
    static member SetHttpHeader(ctx: HttpContext, key: string, value: string) = ctx.Response.Headers[key] <- value

    /// <summary>
    /// Sets the Content-Type HTTP header in the response.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="contentType">The mime type of the response (e.g.: application/json or text/html).</param>
    [<Extension>]
    static member SetContentType(ctx: HttpContext, contentType: string) =
        ctx.SetHttpHeader(HeaderNames.ContentType, contentType)

    /// <summary>
    /// Writes a byte array to the body of the HTTP response and sets the HTTP Content-Length header accordingly.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="bytes">The byte array to be sent back to the client.</param>
    /// <returns>Task of writing to the body of the response.</returns>
    [<Extension>]
    static member WriteBytes(ctx: HttpContext, bytes: byte array) =
        let cancellationToken = ctx.RequestAborted
        // fail before anything is set or written, also for HEAD, once the request has been aborted
        cancellationToken.ThrowIfCancellationRequested()
        ctx.Response.ContentLength <- bytes.LongLength
        if ctx.Request.Method <> HttpMethods.Head then
            ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length, cancellationToken)
        else
            Task.CompletedTask

    /// <summary>
    /// Writes an UTF-8 encoded string to the body of the HTTP response and sets the HTTP `Content-Length` header accordingly, as well as the `Content-Type` header to `text/plain`.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="str">The string value to be send back to the client.</param>
    /// <returns>Task of writing to the body of the response.</returns>
    [<Extension>]
    static member WriteText(ctx: HttpContext, str: string) =
        ctx.SetContentType "text/plain; charset=utf-8"
        let byteCount = Encoding.UTF8.GetByteCount str
        let cancellationToken = ctx.RequestAborted
        // fail before anything is set or written, also for HEAD, once the request has been aborted
        cancellationToken.ThrowIfCancellationRequested()
        ctx.Response.ContentLength <- int64 byteCount
        if ctx.Request.Method <> HttpMethods.Head then
            // encode into a pooled buffer instead of allocating a new byte array per response
            let buffer = ArrayPool<byte>.Shared.Rent byteCount
            let written = Encoding.UTF8.GetBytes(str, 0, str.Length, buffer, 0)
            let writeTask = ctx.Response.Body.WriteAsync(buffer, 0, written, cancellationToken)
            if writeTask.IsCompletedSuccessfully then
                ArrayPool<byte>.Shared.Return buffer
                writeTask
            else
                task {
                    try
                        do! writeTask
                    finally
                        ArrayPool<byte>.Shared.Return buffer
                }
        else
            Task.CompletedTask

    /// <summary>
    /// Serializes an object to JSON and writes the output to the body of the HTTP response.
    /// It also sets the HTTP Content-Type header to application/json and sets the Content-Length header accordingly.
    /// The JSON serializer can be configured in the ASP.NET Core startup code by registering a custom class of type <see cref="Json.ISerializer"/>
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="value">The object to be sent back to the client.</param>
    /// <returns>Task of writing to the body of the response.</returns>
    [<Extension>]
    static member WriteJson<'T>(ctx: HttpContext, value: 'T) =
        let serializer = ctx.GetJsonSerializer()
        serializer.Serialize(value, ctx, false)

    /// <summary>
    /// Serializes an object to JSON and writes the output to the body of the HTTP response using chunked transfer encoding.
    /// It also sets the HTTP Content-Type header to application/json and sets the Transfer-Encoding header to chunked.
    /// The JSON serializer can be configured in the ASP.NET Core startup code by registering a custom class of type <see cref="Json.ISerializer"/>.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="value">The object to be sent back to the client.</param>
    /// <returns>Task of writing to the body of the response.</returns>
    [<Extension>]
    static member WriteJsonChunked<'T>(ctx: HttpContext, value: 'T) =
        let serializer = ctx.GetJsonSerializer()
        serializer.Serialize(value, ctx, true)

    /// <summary>
    /// <para>Compiles an `HtmlElement` object to a HTML view and writes the output to the body of the HTTP response.</para>
    /// <para>It also sets the HTTP header `Content-Type` to `text/html` and sets the `Content-Length` header accordingly.</para>
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="htmlView">An `HtmlElement` object to be send back to the client and which represents a valid HTML view.</param>
    /// <returns>Task of writing to the body of the response.</returns>
    [<Extension>]
    static member WriteHtmlView(ctx: HttpContext, htmlView: #HtmlElement) =
        let cancellationToken = ctx.RequestAborted
        cancellationToken.ThrowIfCancellationRequested()
        let memoryStream = PooledBufferStream.Rent()
        ctx.Response.ContentType <- "text/html; charset=utf-8"
        task {
            try
                // renders like Render.toHtmlDocStreamAsync, but encodes straight into the pooled buffer
                let sb = Tools.StringBuilderPool.Get()
                try
                    sb.AppendLine("<!DOCTYPE html>") |> ignore
                    htmlView.Render sb
                    HtmlWriting.encodeStrictUtf8 sb memoryStream
                finally
                    Tools.StringBuilderPool.Return(sb)
                cancellationToken.ThrowIfCancellationRequested()
                ctx.Response.ContentLength <- memoryStream.Length
                if ctx.Request.Method <> HttpMethods.Head then
                    return! ctx.Response.Body.WriteAsync(memoryStream.WrittenMemory, cancellationToken)
            finally
                memoryStream.Return()
        }
        :> Task

    /// <summary>
    /// <para>Serializes a stream of HTML elements and writes the output to the body of the HTTP response using chunked transfer encoding.</para>
    /// <para>It also sets the HTTP header `Content-Type` to `text/html` and sets the Transfer-Encoding header to chunked.</para>
    /// <para>Each element is encoded into the response `BodyWriter` and flushed with `ctx.RequestAborted`, which is also passed to `GetAsyncEnumerator`
    /// and checked before the enumeration and after every `MoveNextAsync`, so nothing is rendered and the response is not completed
    /// once the request has been aborted, even if the source ignores the token.</para>
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="htmlStream">An `HtmlElement` stream to be send back to the client.</param>
    /// <returns>Task of writing to the body of the response.</returns>
    [<Extension>]
    static member WriteHtmlChunked(ctx: HttpContext, htmlStream: #IAsyncEnumerable<#HtmlElement>) =
        ctx.Response.ContentType <- "text/html; charset=utf-8"
        let cancellationToken = ctx.RequestAborted
        let writer = ctx.Response.BodyWriter
        task {
            // the source may ignore the token it is given: fail before enumerating an already aborted request and
            // after every MoveNextAsync, so that once the request has been aborted no element is rendered and the
            // response is not completed either
            cancellationToken.ThrowIfCancellationRequested()
            let enumerator = htmlStream.GetAsyncEnumerator(cancellationToken)
            use _ = enumerator :> IAsyncDisposable
            while! enumerator.MoveNextAsync() do
                cancellationToken.ThrowIfCancellationRequested()
                Render.toBufferWriter(writer, enumerator.Current)
                let! _ = writer.FlushAsync(cancellationToken)
                ()
            cancellationToken.ThrowIfCancellationRequested()
        }

    /// <summary>
    /// <para>Serializes an HTML element object and writes the output to the body of the HTTP response using chunked transfer encoding.</para>
    /// <para>It also sets the HTTP header `Content-Type` to `text/html` and sets the Transfer-Encoding header to chunked.</para>
    /// <para>The document is encoded into the response `BodyWriter` and flushed with `ctx.RequestAborted`; nothing is rendered when the request is already aborted.</para>
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="htmlElement">An `HtmlElement` object to be send back to the client.</param>
    /// <returns>Task of writing to the body of the response.</returns>
    [<Extension>]
    static member WriteHtmlViewChunked(ctx: HttpContext, htmlElement: #HtmlElement) =
        ctx.Response.ContentType <- "text/html; charset=utf-8"
        let cancellationToken = ctx.RequestAborted
        let writer = ctx.Response.BodyWriter
        task {
            cancellationToken.ThrowIfCancellationRequested()
            Render.toHtmlDocBufferWriter(writer, htmlElement)
            let! _ = writer.FlushAsync(cancellationToken)
            ()
        }

    /// <summary>
    /// Executes and ASP.NET Core IResult. Note that in most cases the response will be chunked.
    /// </summary>
    [<Extension>]
    static member Write(ctx: HttpContext, result: #IResult) = result.ExecuteAsync(ctx)

    /// <summary>
    /// Uses the <see cref="IJsonSerializer"/> to deserialize the entire body of the <see cref="Microsoft.AspNetCore.Http.HttpRequest"/> asynchronously into an object of type 'T.
    /// </summary>
    /// <typeparam name="'T"></typeparam>
    /// <returns>Returns a <see cref="System.Threading.Tasks.Task{T}"/></returns>
    [<Extension>]
    static member BindJson<'T>(ctx: HttpContext) =
        let serializer = ctx.GetJsonSerializer()
        let deserialized =
            try
                serializer.Deserialize<'T>(ctx)
            with ex ->
                // handled below exactly like an asynchronous failure
                Task.FromException<'T> ex
        if deserialized.IsCompletedSuccessfully then
            // nothing to wrap: hand out the completed task instead of allocating another one
            deserialized
        else
            task {
                try
                    return! deserialized
                with ex when not(ex :? OperationCanceledException) ->
                    return raise <| ModelBindException("Unable to deserialize model from JSON", ex)
            }

    /// <summary>
    /// Parses all input elements from an HTML form into an object of type 'T.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <typeparam name="'T"></typeparam>
    /// <returns>Returns a <see cref="System.Threading.Tasks.Task{T}"/></returns>
    [<Extension>]
    static member BindForm<'T>(ctx: HttpContext) =
        let binder = ctx.GetModelBinder()
        // CSRF check
        let feature = ctx.Features.Get<IAntiforgeryValidationFeature>()
        match feature with
        | null -> ()
        | f ->
            match f.IsValid, f.Error with
            | true, _
            | false, Null -> ()
            | false, NonNull err -> ExceptionDispatchInfo.Throw err
        task {
            try
                let! form = ctx.Request.ReadFormAsync(ctx.RequestAborted)
                return binder.Bind<'T> form
            with ex when not(ex :? OperationCanceledException) ->
                return raise <| ModelBindException("Unable to deserialize model from form", ex)
        }

    /// <summary>
    /// Parses all parameters of a request's query string into an object of type 'T.
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <typeparam name="'T"></typeparam>
    /// <returns>Returns an instance of type 'T</returns>
    [<Extension>]
    static member BindQuery<'T>(ctx: HttpContext) =
        try
            let binder = ctx.GetModelBinder()
            match binder with
            // Nothing has read Request.Query yet: the default binder parses the query string itself, so the request
            // does not build the query collection only to have it copied
            | :? ModelBinder as defaultBinder when
                ctx :? DefaultHttpContext
                && binder.GetType() = typeof<ModelBinder>
                && isNull(box(ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IQueryFeature>()))
                ->
                defaultBinder.BindQueryString<'T>(ctx.Request.QueryString.Value)
            | _ -> binder.Bind<'T> ctx.Request.Query
        with ex ->
            raise <| ModelBindException("Unable to deserialize model from query", ex)

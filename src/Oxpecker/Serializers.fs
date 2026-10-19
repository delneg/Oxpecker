namespace Oxpecker

open System
open System.IO
open System.IO.Pipelines
open System.Runtime.ExceptionServices
open Microsoft.AspNetCore.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.IO

/// <summary>
/// <para>Interface defining JSON serialization methods.
/// Use this interface to customize JSON serialization in Oxpecker.</para>
/// <para>`Serialize` writes a value as the JSON response and `Deserialize` reads a value from the request body, while
/// `SerializePart` writes a value as UTF-8 JSON to a `PipeWriter` without touching the response, e.g. as the body of a
/// multipart part created with `MultipartPart.Json`.</para>
/// <para>Implementations must pass `ctx.RequestAborted`, or the token given to `SerializePart`, to every asynchronous read or write,
/// so that serialization stops when the client disconnects, and let the resulting `OperationCanceledException` propagate.</para>
/// </summary>
type IJsonSerializer =
    /// <summary>
    /// Writes the value as JSON to the response: sets the `Content-Type` header (and `Content-Length`, unless chunked) and writes the body.
    /// </summary>
    abstract member Serialize<'T> : value: 'T * ctx: HttpContext * chunked: bool -> Task
    /// <summary>
    /// This is only used by multipart serialization to write a single JSON part.
    /// Writes the value as UTF-8 JSON to the writer: only the JSON body, no response headers are set. The writer must not be completed
    /// (a serializer that needs a `Stream` can use `writer.AsStream(leaveOpen = true)`) and does not have to be flushed, the caller flushes it.
    /// The token has to be passed to every asynchronous write or flush.
    /// </summary>
    abstract member SerializePart<'T> : value: 'T * writer: PipeWriter * cancellationToken: CancellationToken -> Task
    /// <summary>
    /// Reads a value from the JSON request body.
    /// </summary>
    abstract member Deserialize<'T> : ctx: HttpContext -> Task<'T>

/// <summary>
/// <see cref="Serializers.SystemTextJsonSerializer" /> is a default implementation of  <see cref="Serializers.IJsonSerializer"/> in Oxpecker.
///
/// It uses <see cref="System.Text.Json"/> as the underlying JSON serializer to (de-)serialize
/// JSON content.
/// For support of F# unions and records, look at https://github.com/Tarmil/FSharp.SystemTextJson
/// which plugs into this serializer.
/// </summary>
type SystemTextJsonSerializer(?options: JsonSerializerOptions) =
    let options = defaultArg options <| JsonSerializerOptions(JsonSerializerDefaults.Web)

    let serializeToStreamWithLength
        value
        (stream: PooledBufferStream)
        (ctx: HttpContext)
        (options: JsonSerializerOptions)
        =
        ctx.RequestAborted.ThrowIfCancellationRequested()
        JsonSerializer.Serialize(stream, value, options)
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.Headers.ContentLength <- stream.Length
        if ctx.Request.Method <> HttpMethods.Head then
            ctx.Response.Body.WriteAsync(stream.WrittenMemory, ctx.RequestAborted).AsTask()
        else
            Task.CompletedTask

    interface IJsonSerializer with
        member this.Serialize(value, ctx, chunked) =
            if chunked then
                let cancellationToken = ctx.RequestAborted
                // fail before anything is enumerated or written, also for HEAD, once the request has been aborted:
                // a stream of values may ignore the token it is given
                cancellationToken.ThrowIfCancellationRequested()
                if ctx.Request.Method <> HttpMethods.Head then
                    ctx.Response.WriteAsJsonAsync(value, options, cancellationToken)
                else
                    ctx.Response.ContentType <- "application/json; charset=utf-8"
                    Task.CompletedTask
            else
                task {
                    // a pooled buffer instead of a RecyclableMemoryStream with its block list per response
                    let stream = PooledBufferStream.Rent()
                    try
                        return! serializeToStreamWithLength value stream ctx options
                    finally
                        stream.Return()
                }

        member this.SerializePart(value, writer, cancellationToken) =
            JsonSerializer.SerializeAsync(writer, value, options, cancellationToken)

        member this.Deserialize(ctx) = this.DeserializeValue(ctx).AsTask()

    /// Deserialize without a Task<'T> when the body has already been read: the same results, exceptions and cancellation
    member internal this.DeserializeValue<'T>(ctx: HttpContext) : ValueTask<'T> =
        let request = ctx.Request
        // A JSON content type in UTF-8: what HttpRequest.ReadFromJsonAsync does, but reading Request.Body, as ASP.NET
        // Core does with its Microsoft.AspNetCore.UseStreamBasedJsonParsing switch. In .NET 11 RC 2 the PipeReader
        // overload of JsonSerializer.DeserializeAsync allocates on every call: PipeReadBufferState.Dispose compares
        // ReadOnlySequence values with ValueType.Equals (boxing and reflection). Other content types and charsets keep
        // ReadFromJsonAsync, with its transcoding and errors.
        let contentType = request.ContentType
        let isUtf8Json =
            // the two usual spellings without parsing (MediaTypeHeaderValue gives the same answer for them)
            if
                String.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase)
                || String.Equals(contentType, "application/json; charset=utf-8", StringComparison.OrdinalIgnoreCase)
            then
                true
            else
                let mutable mediaType = null
                if Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, &mediaType) then
                    match mediaType with
                    | null -> false
                    | mediaType ->
                        (mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                         || mediaType.Suffix.Equals("json", StringComparison.OrdinalIgnoreCase))
                        && (not mediaType.Charset.HasValue
                            || mediaType.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                else
                    false
        try
            let pending =
                if isUtf8Json then
                    JsonSerializer.DeserializeAsync<'T>(request.Body, options, ctx.RequestAborted)
                else
                    request.ReadFromJsonAsync<'T>(options, ctx.RequestAborted)
            if pending.IsCompletedSuccessfully then
                match pending.Result with
                | null -> ValueTask<'T>(Unchecked.defaultof<'T>)
                | v -> ValueTask<'T>(v)
            else
                ValueTask<'T>(
                    task {
                        match! pending with
                        | null -> return Unchecked.defaultof<_>
                        | v -> return v
                    }
                )
        with ex ->
            // what task { } gave for an exception before its first await: cancelled for OperationCanceledException
            let thrown = ExceptionDispatchInfo.Capture ex
            ValueTask<'T>(
                task {
                    thrown.Throw()
                    return Unchecked.defaultof<'T>
                }
            )

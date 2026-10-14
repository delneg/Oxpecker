namespace Oxpecker

open System
open System.Buffers
open System.IO
open Microsoft.IO

/// <summary>
/// Write-only stream over a single ArrayPool buffer that grows as needed; the buffer goes back to the pool on Dispose.
/// Lets a response body be measured (Content-Length) before it is written, with one small object per response.
/// </summary>
[<Sealed>]
type internal PooledBufferStream() =
    inherit Stream()
    let mutable buffer: byte array = Array.Empty<byte>()
    let mutable length = 0
    member _.WrittenMemory = ReadOnlyMemory<byte>(buffer, 0, length)
    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = int64 length
    override _.Position
        with get () = int64 length
        and set _ = raise(NotSupportedException())
    override _.Flush() = ()
    override _.Read(_: byte array, _: int, _: int) : int = raise(NotSupportedException())
    override _.Seek(_: int64, _: SeekOrigin) : int64 = raise(NotSupportedException())
    override _.SetLength(_: int64) = raise(NotSupportedException())
    override _.Write(source: ReadOnlySpan<byte>) =
        if source.Length > buffer.Length - length then
            let newBuffer = ArrayPool<byte>.Shared.Rent(max (length + source.Length) (buffer.Length * 2))
            buffer.AsSpan(0, length).CopyTo(newBuffer)
            if buffer.Length > 0 then
                ArrayPool<byte>.Shared.Return buffer
            buffer <- newBuffer
        source.CopyTo(buffer.AsSpan(length))
        length <- length + source.Length
    override this.Write(source: byte array, offset: int, count: int) =
        this.Write(ReadOnlySpan<byte>(source, offset, count))
    override _.Dispose(disposing: bool) =
        if buffer.Length > 0 then
            ArrayPool<byte>.Shared.Return buffer
        buffer <- Array.Empty<byte>()
        length <- 0

[<AutoOpen>]
module Helpers =

    /// <summary>Default single RecyclableMemoryStreamManager.</summary>
    let internal recyclableMemoryStreamManager = Lazy<RecyclableMemoryStreamManager>()

    /// Same as << but with two arguments
    let inline (<<+) func2 func1 x y = func2(func1 x y)

    /// Same as << but with three arguments
    let inline (<<++) func2 func1 x y z = func2(func1 x y z)

    /// <summary>
    /// Utility function for matching 1xx HTTP status codes.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>Returns true if the status code is between 100 and 199.</returns>
    let is1xxStatusCode (statusCode: int) = 100 <= statusCode && statusCode <= 199

    /// <summary>
    /// Utility function for matching 2xx HTTP status codes.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>Returns true if the status code is between 200 and 299.</returns>
    let is2xxStatusCode (statusCode: int) = 200 <= statusCode && statusCode <= 299

    /// <summary>
    /// Utility function for matching 3xx HTTP status codes.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>Returns true if the status code is between 300 and 399.</returns>
    let is3xxStatusCode (statusCode: int) = 300 <= statusCode && statusCode <= 399

    /// <summary>
    /// Utility function for matching 4xx HTTP status codes.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>Returns true if the status code is between 400 and 499.</returns>
    let is4xxStatusCode (statusCode: int) = 400 <= statusCode && statusCode <= 499

    /// <summary>
    /// Utility function for matching 5xx HTTP status codes.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>Returns true if the status code is between 500 and 599.</returns>
    let is5xxStatusCode (statusCode: int) = 500 <= statusCode && statusCode <= 599

    /// <summary>
    /// Boxes value with return type obj (not objnull).
    /// </summary>
    let inline boxv<'a when 'a: struct> (v: 'a) = box v |> Unchecked.nonNull

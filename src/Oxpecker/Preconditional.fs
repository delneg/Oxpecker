[<AutoOpen>]
module Oxpecker.Preconditional

open System
open System.Linq
open System.Runtime.CompilerServices
open System.Threading
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Headers
open Microsoft.Extensions.Primitives
open Microsoft.Net.Http.Headers

let inline private cutOffMs (dt: DateTimeOffset) =
    DateTimeOffset(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, 0, dt.Offset)

[<Sealed>]
type private FormattedDate(date: DateTimeOffset, text: string) =
    member _.Date = date
    member _.Text = text

/// Last-Modified values are usually a few fixed dates (one per handler), so the first few formatted dates
/// are kept (each slot is filled once and never replaced) and reused instead of formatting a new string per request.
module private LastModifiedCache =
    let private slots: (FormattedDate | null) array = Array.zeroCreate 8

    let format (date: DateTimeOffset) : string =
        let mutable result: string | null = null
        let mutable i = 0
        while isNull result && i < slots.Length do
            match Volatile.Read(&slots[i]) with
            | null ->
                let text = HeaderUtilities.FormatDate date
                Interlocked.CompareExchange(&slots[i], FormattedDate(date, text), null)
                |> ignore
                result <- text
            | slot when slot.Date.EqualsExact date -> result <- slot.Text
            | _ -> ()
            i <- i + 1
        match result with
        | null -> HeaderUtilities.FormatDate date
        | text -> text

[<Struct>]
type Precondition =
    | NoConditionsSpecified
    | ResourceNotModified
    | ConditionFailed
    | AllConditionsMet

[<AutoOpen>]
module EntityTagHelper =
    /// <summary>
    /// Creates an object of type <see cref="EntityTagHeaderValue"/>.
    /// </summary>
    /// <param name="isWeak">The difference between a regular (strong) ETag and a weak ETag is that a matching strong ETag guarantees the file is byte-for-byte identical, whereas a matching weak ETag indicates that the content is semantically the same. So if the content of the file changes, the weak ETag should change as well.</param>
    /// <param name="eTag">The entity tag value (without quotes or the W/ prefix).</param>
    /// <returns>Returns an object of <see cref="EntityTagHeaderValue"/>.</returns>
    let private fromString (isWeak: bool) (eTag: string) =
        let eTagValue = $"\"%s{eTag}\""
        EntityTagHeaderValue(StringSegment(eTagValue), isWeak)

    let createETag (eTag: string) = fromString false eTag

    let createWeakETag (eTag: string) = fromString true eTag

type HttpContext with

    member private this.IsHeadOrGetRequest() =
        HttpMethods.IsHead this.Request.Method || HttpMethods.IsGet this.Request.Method

    member private this.ValidateIfMatch (eTag: EntityTagHeaderValue option) (requestHeaders: RequestHeaders) =
        match requestHeaders.IfMatch.Any() with
        | false -> NoConditionsSpecified
        | true ->
            match eTag with
            | None -> ConditionFailed
            | Some eTag ->
                requestHeaders.IfMatch
                |> Seq.exists _.Compare(eTag, true)
                |> function
                    | true -> AllConditionsMet
                    | false -> ConditionFailed

    member private this.ValidateIfUnmodifiedSince
        (lastModified: DateTimeOffset option)
        (requestHeaders: RequestHeaders)
        =
        match requestHeaders.IfUnmodifiedSince.HasValue with
        | false -> NoConditionsSpecified
        | true ->
            match lastModified with
            | None -> AllConditionsMet
            | Some lastModified ->
                let lastModified = cutOffMs lastModified
                match
                    requestHeaders.IfUnmodifiedSince.Value > (cutOffMs DateTimeOffset.UtcNow)
                    || requestHeaders.IfUnmodifiedSince.Value >= lastModified
                with
                | true -> AllConditionsMet
                | false -> ConditionFailed

    member private this.ValidateIfNoneMatch (eTag: EntityTagHeaderValue option) (requestHeaders: RequestHeaders) =
        match requestHeaders.IfNoneMatch.Any() with
        | false -> NoConditionsSpecified
        | true ->
            match eTag with
            | None -> AllConditionsMet
            | Some eTag ->
                requestHeaders.IfNoneMatch
                |> Seq.exists _.Compare(eTag, false)
                |> function
                    | false -> AllConditionsMet
                    | true ->
                        match this.IsHeadOrGetRequest() with
                        | true -> ResourceNotModified
                        | false -> ConditionFailed

    member private this.ValidateIfModifiedSince (lastModified: DateTimeOffset option) (requestHeaders: RequestHeaders) =
        match requestHeaders.IfModifiedSince.HasValue && this.IsHeadOrGetRequest() with
        | false -> NoConditionsSpecified
        | true ->
            match lastModified with
            | None -> AllConditionsMet
            | Some lastModified ->
                let lastModified = cutOffMs lastModified
                match
                    requestHeaders.IfModifiedSince.Value <= (cutOffMs DateTimeOffset.UtcNow)
                    && requestHeaders.IfModifiedSince.Value < lastModified
                with
                | true -> AllConditionsMet
                | false -> ResourceNotModified

type PreconditionExtensions() =
    /// <summary>
    /// Validates the following conditional HTTP headers of the HTTP request:
    ///
    /// If-Match
    ///
    /// If-None-Match
    ///
    /// If-Modified-Since
    ///
    /// If-Unmodified-Since
    /// </summary>
    /// <param name="ctx">The current http context object.</param>
    /// <param name="eTag">Optional ETag. You can use the static EntityTagHeaderValue.FromString helper method to generate a valid <see cref="EntityTagHeaderValue"/> object.</param>
    /// <param name="lastModified">Optional <see cref="System.DateTimeOffset"/> object denoting the last modified date.</param>
    /// <returns>
    /// Returns a Precondition union type, which can have one of the following cases:
    ///
    /// NoConditionsSpecified: No validation has taken place, because the client didn't send any conditional HTTP headers.
    ///
    /// ConditionFailed: At least one condition couldn't be satisfied. It is advised to return a 412 status code back to the client (you can use the HttpContext.PreconditionFailedResponse() method for that purpose).
    ///
    /// ResourceNotModified: The resource hasn't changed since the last visit. The server can skip processing this request and return a 304 status code back to the client (you can use the HttpContext.NotModifiedResponse() method for that purpose).
    ///
    /// AllConditionsMet: All pre-conditions can be satisfied. The server should continue processing the request as normal.
    /// </returns>
    [<Extension>]
    static member ValidatePreconditions
        (ctx: HttpContext, eTag: EntityTagHeaderValue option, lastModified: DateTimeOffset option)
        =
        // Set ETag and Last-Modified in the response
        // (same strings as the typed ResponseHeaders setters, without allocating the typed wrapper)
        let responseHeaders = ctx.Response.Headers
        if eTag.IsSome then
            responseHeaders.ETag <- StringValues(eTag.Value.ToString())
        if lastModified.IsSome then
            responseHeaders.LastModified <- StringValues(LastModifiedCache.format(cutOffMs lastModified.Value))
        PreconditionExtensions.ValidateRequestPreconditions(ctx, eTag, lastModified)

    /// Validates the conditional request headers only (the response headers are set by the caller).
    static member internal ValidateRequestPreconditions
        (ctx: HttpContext, eTag: EntityTagHeaderValue option, lastModified: DateTimeOffset option)
        =
        let rawHeaders = ctx.Request.Headers
        if
            rawHeaders.IfMatch.Count = 0
            && rawHeaders.IfNoneMatch.Count = 0
            && rawHeaders.IfModifiedSince.Count = 0
            && rawHeaders.IfUnmodifiedSince.Count = 0
        then
            // No conditional headers: every validator below would return NoConditionsSpecified
            NoConditionsSpecified
        else
            let requestHeaders = ctx.Request.GetTypedHeaders()

            // Validate headers in correct precedence
            // RFC: https://tools.ietf.org/html/rfc7232#section-6
            // (plain matches instead of chained helper functions, so no closures are allocated per request)
            let afterIfUnmodifiedSince =
                match ctx.ValidateIfMatch eTag requestHeaders with
                | NoConditionsSpecified -> ctx.ValidateIfUnmodifiedSince lastModified requestHeaders
                | result -> result
            let afterIfNoneMatch =
                match afterIfUnmodifiedSince with
                | NoConditionsSpecified -> ctx.ValidateIfNoneMatch eTag requestHeaders
                | AllConditionsMet ->
                    match ctx.ValidateIfNoneMatch eTag requestHeaders with
                    | NoConditionsSpecified -> AllConditionsMet
                    | result -> result
                | result -> result
            match afterIfNoneMatch with
            | NoConditionsSpecified -> ctx.ValidateIfModifiedSince lastModified requestHeaders
            | result -> result

    [<Extension>]
    static member NotModifiedResponse(ctx: HttpContext) =
        ctx.SetStatusCode StatusCodes.Status304NotModified

    /// <summary>
    /// Sends a default HTTP 412 Precondition Failed response to the client.
    /// </summary>
    /// <returns></returns>
    [<Extension>]
    static member PreconditionFailedResponse(ctx: HttpContext) =
        ctx.SetStatusCode StatusCodes.Status412PreconditionFailed

// ---------------------------
// EndpointHandler functions
// ---------------------------

/// <summary>
/// Validates the following conditional HTTP headers of the request:
///
/// If-Match
///
/// If-None-Match
///
/// If-Modified-Since
///
/// If-Unmodified-Since
///
///
/// If the conditions are met (or non existent) then it will invoke the next http handler in the pipeline otherwise it will return a 304 Not Modified or 412 Precondition Failed response.
/// </summary>
/// <param name="eTag">Optional ETag. You can use the static EntityTagHeaderValue.FromString helper method to generate a valid <see cref="EntityTagHeaderValue"/> object.</param>
/// <param name="lastModified">Optional <see cref="System.DateTimeOffset"/> object denoting the last modified date.</param>
/// <param name="next"></param>
/// <param name="ctx"></param>
/// <returns>An Oxpecker <see cref="HttpHandler" /> function which can be composed into a bigger web application.</returns>
let validatePreconditions
    (eTag: EntityTagHeaderValue option)
    (lastModified: DateTimeOffset option)
    : EndpointMiddleware =
    fun (next: EndpointHandler) (ctx: HttpContext) ->
        task {
            match ctx.ValidatePreconditions(eTag, lastModified) with
            | ConditionFailed -> return ctx.PreconditionFailedResponse()
            | ResourceNotModified -> return ctx.NotModifiedResponse()
            | AllConditionsMet
            | NoConditionsSpecified -> return! next ctx
        }

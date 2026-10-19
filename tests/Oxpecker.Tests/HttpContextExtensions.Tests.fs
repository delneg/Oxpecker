module Oxpecker.Tests.HttpContextExtensions

open System
open System.Collections.Generic
open System.IO
open System.Text
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Microsoft.AspNetCore.WebUtilities
open Microsoft.Extensions.DependencyInjection
open Oxpecker.ViewEngine
open Xunit
open FsUnit.Light
open Oxpecker


#nowarn "3391"

type StringCollectionModel = { Tags: string list }

type ScalarCollectionModel = {
    Counts: int array
    Ratios: float list
    Flags: bool seq
}

type EmptyInputModel = {
    Id: Guid
    Count: int
    Enabled: bool
    Name: string | null
    Search: string option
}

let private emptyInputModel = {
    Id = Guid.Empty
    Count = 0
    Enabled = false
    Name = null
    Search = None
}

let private createContext () =
    let ctx = DefaultHttpContext()
    let services = ServiceCollection()
    services.AddSingleton<IModelBinder>(ModelBinder()) |> ignore
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx

let private createFormContext (body: string) =
    let ctx = createContext()
    ctx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes body)
    ctx.Request.ContentType <- "application/x-www-form-urlencoded"
    ctx

[<Fact>]
let ``GetRequestUrl returns entire URL of the HTTP request`` () =
    let ctx = DefaultHttpContext()
    ctx.Request.Scheme <- "http"
    ctx.Request.Host <- HostString("example.org:81")
    ctx.Request.PathBase <- PathString("/something")
    ctx.Request.Path <- PathString("/hello")
    ctx.Request.QueryString <- QueryString("?a=1&b=2")
    ctx.Request.Method <- "GET"
    ctx.Response.Body <- new MemoryStream()

    let result = ctx.GetRequestUrl()

    result |> shouldEqual "http://example.org:81/something/hello?a=1&b=2"

[<Fact>]
let ``TryGetRequestHeader during HTTP GET request with returns correct result`` () =
    let ctx = DefaultHttpContext()
    ctx.TryGetHeaderValue "X-Test" |> shouldEqual None
    ctx.Request.Headers.Add("X-Test", "It works!")

    let result = ctx.TryGetHeaderValue "X-Test"

    result |> shouldEqual(Some "It works!")

[<Fact>]
let ``TryGetQueryStringValue during HTTP GET request with query string returns correct result`` () =
    let ctx = DefaultHttpContext()
    ctx.TryGetQueryValue "BirthDate" |> shouldEqual None
    let queryStr = "?Name=John%20Doe&IsVip=true&BirthDate=1990-04-20&Balance=150000.5&LoyaltyPoints=137"
    let query = QueryHelpers.ParseQuery queryStr
    ctx.Request.Query <- QueryCollection(query)

    let result = ctx.TryGetQueryValue "BirthDate"

    result |> shouldEqual(Some "1990-04-20")

[<Fact>]
let ``ReadFormAsync groups repeated string collection values`` () =
    task {
        let ctx = createFormContext "tags=dotnet&tags=mvc&tags=api"

        let! form = ctx.Request.ReadFormAsync()

        form.Keys |> Seq.toList |> shouldEqual [ "tags" ]
        form["tags"] |> Seq.toList |> shouldEqual [ "dotnet"; "mvc"; "api" ]
    }

[<Fact>]
let ``ReadFormAsync preserves indexed string collection keys`` () =
    task {
        let ctx = createFormContext "tags[0]=dotnet&tags[1]=mvc&tags[2]=api"

        let! form = ctx.Request.ReadFormAsync()

        form.Keys |> Set.ofSeq |> shouldEqual(set [ "tags[0]"; "tags[1]"; "tags[2]" ])
        form["tags[0]"] |> string |> shouldEqual "dotnet"
        form["tags[1]"] |> string |> shouldEqual "mvc"
        form["tags[2]"] |> string |> shouldEqual "api"
    }

[<Fact>]
let ``BindForm binds repeated and indexed string collections`` () =
    task {
        let repeatedContext = createFormContext "Tags=dotnet&Tags=mvc&Tags=api"
        let indexedContext = createFormContext "Tags[0]=dotnet&Tags[1]=mvc&Tags[2]=api"

        let! repeated = repeatedContext.BindForm<StringCollectionModel>()
        let! indexed = indexedContext.BindForm<StringCollectionModel>()

        repeated.Tags |> shouldEqual [ "dotnet"; "mvc"; "api" ]
        indexed.Tags |> shouldEqual repeated.Tags
    }

[<Fact>]
let ``BindForm binds indexed scalar collections`` () =
    task {
        let ctx = createFormContext "Counts[0]=1&Counts[1]=2&Ratios[0]=1.5&Ratios[1]=2.25&Flags[0]=true&Flags[1]=false"

        let! result = ctx.BindForm<ScalarCollectionModel>()

        result.Counts |> shouldEqual [| 1; 2 |]
        result.Ratios |> shouldEqual [ 1.5; 2.25 ]
        result.Flags |> shouldEqual(seq [ true; false ])
    }

[<Fact>]
let ``BindForm binds first value for duplicated collection index`` () =
    task {
        // The checkbox + hidden-input fallback idiom: both inputs share the same indexed name.
        let ctx = createFormContext "Flags[0]=true&Flags[0]=false&Flags[1]=false"

        let! result = ctx.BindForm<ScalarCollectionModel>()

        result.Flags |> shouldEqual(seq [ true; false ])
    }

[<Fact>]
let ``BindQuery binds indexed string collections`` () =
    let ctx = createContext()
    ctx.Request.Query <- QueryCollection(QueryHelpers.ParseQuery "?Tags[0]=dotnet&Tags[1]=mvc&Tags[2]=api")

    let result = ctx.BindQuery<StringCollectionModel>()

    result.Tags |> shouldEqual [ "dotnet"; "mvc"; "api" ]

[<Theory>]
[<InlineData("")>]
[<InlineData("?")>]
[<InlineData("?unrelated=value")>]
let ``BindQuery binds defaults without matching query parameters`` (queryString: string) =
    let ctx = createContext()
    ctx.Request.QueryString <- QueryString(queryString)

    let result = ctx.BindQuery<EmptyInputModel>()
    let collection = ctx.BindQuery<EmptyInputModel array>()

    result |> shouldEqual emptyInputModel
    collection |> shouldEqual [||]

[<Theory>]
[<InlineData("application/x-www-form-urlencoded", true)>]
[<InlineData("application/x-www-form-urlencoded", false)>]
[<InlineData("multipart/form-data; boundary=test", true)>]
let ``BindForm binds defaults from an empty body`` (contentType: string, hasContentLength: bool) =
    task {
        let ctx = createFormContext ""
        ctx.Request.ContentType <- contentType
        if hasContentLength then
            ctx.Request.ContentLength <- Nullable 0L

        let! result = ctx.BindForm<EmptyInputModel>()
        let! collection = ctx.BindForm<EmptyInputModel array>()

        result |> shouldEqual emptyInputModel
        collection |> shouldEqual [||]
    }

[<Theory>]
[<InlineData(null)>]
[<InlineData("text/plain")>]
let ``BindForm rejects an empty body without a form content type`` (contentType: string | null) =
    task {
        let ctx = createFormContext ""
        ctx.Request.ContentType <- contentType
        ctx.Request.ContentLength <- Nullable 0L

        let! ex =
            Assert.ThrowsAsync<ModelBindException>(fun () ->
                ctx.BindForm<EmptyInputModel>() :> System.Threading.Tasks.Task)

        Assert.IsType<InvalidOperationException>(ex.InnerException) |> ignore
    }

[<Fact>]
let ``WriteText with HTTP GET should return text in body`` () =
    task {
        let ctx = DefaultHttpContext()
        ctx.Response.Body <- new MemoryStream()

        do! ctx.WriteText "Hello World"

        ctx.Response.Body.Seek(0, SeekOrigin.Begin) |> ignore
        use reader = new StreamReader(ctx.Response.Body)
        let result = reader.ReadToEnd()
        result |> shouldEqual "Hello World"
    }

[<Fact>]
let ``WriteText with HTTP HEAD should not return text in body`` () =
    task {
        let ctx = DefaultHttpContext()
        ctx.Response.Body <- new MemoryStream()
        ctx.Request.Method <- "HEAD"

        do! ctx.WriteText "Hello World"

        ctx.Response.Body.Seek(0, SeekOrigin.Begin) |> ignore
        use reader = new StreamReader(ctx.Response.Body)
        reader.ReadToEnd() |> shouldEqual ""
    }

[<Fact>]
let ``WriteJson should add json to the context`` () =
    task {
        let ctx = DefaultHttpContext()
        ctx.Response.Body <- new MemoryStream()
        let services = ServiceCollection()
        services.AddSingleton<IJsonSerializer>(fun sp -> SystemTextJsonSerializer() :> IJsonSerializer)
        |> ignore
        ctx.RequestServices <- DefaultServiceProviderFactory().CreateServiceProvider(services)

        do! ctx.WriteJson({| Hello = "World" |})

        ctx.Response.Body.Seek(0, SeekOrigin.Begin) |> ignore
        use reader = new StreamReader(ctx.Response.Body)
        let result = reader.ReadToEnd()
        ctx.Response.Headers.ContentType
        |> shouldEqual "application/json; charset=utf-8"
        ctx.Response.Headers.ContentLength |> shouldEqual 17L
        result |> shouldEqual """{"hello":"World"}"""
    }

[<Fact>]
let ``WriteJsonChunked should add json to the context`` () =
    task {
        let ctx = DefaultHttpContext()
        ctx.Response.Body <- new MemoryStream()
        let services = ServiceCollection()
        services.AddSingleton<IJsonSerializer>(fun sp -> SystemTextJsonSerializer() :> IJsonSerializer)
        |> ignore
        ctx.RequestServices <- DefaultServiceProviderFactory().CreateServiceProvider(services)

        do! ctx.WriteJsonChunked {| Hello = "World" |}

        ctx.Response.Body.Seek(0, SeekOrigin.Begin) |> ignore
        use reader = new StreamReader(ctx.Response.Body)
        let result = reader.ReadToEnd()
        ctx.Response.Headers.ContentType
        |> shouldEqual "application/json; charset=utf-8"
        ctx.Response.Headers.ContentLength |> shouldEqual(Nullable())
        result |> shouldEqual """{"hello":"World"}"""
    }

[<Fact>]
let ``WriteHtmlViewAsync should add html to the context`` () =
    task {
        let ctx = DefaultHttpContext()
        ctx.Response.Body <- new MemoryStream()
        let htmlDoc =
            html() {
                head()
                body() { h1(id = "header") { "Hello world" } }
            }
        do! ctx.WriteHtmlView(htmlDoc)

        ctx.Response.Body.Seek(0, SeekOrigin.Begin) |> ignore
        use reader = new StreamReader(ctx.Response.Body)
        let result = reader.ReadToEnd()

        result
        |> shouldEqual
            $"""<!DOCTYPE html>{Environment.NewLine}<html><head></head><body><h1 id="header">Hello world</h1></body></html>"""
    }

[<Fact>]
let ``WriteHtmlViewChunked should add the html document to the context`` () =
    task {
        let ctx = DefaultHttpContext()
        ctx.Response.Body <- new MemoryStream()
        let htmlDoc =
            html() {
                head()
                body() { h1(id = "header") { "Hello world" } }
            }
        do! ctx.WriteHtmlViewChunked(htmlDoc)

        ctx.Response.Body.Seek(0, SeekOrigin.Begin) |> ignore
        use reader = new StreamReader(ctx.Response.Body)
        let result = reader.ReadToEnd()

        result
        |> shouldEqual
            $"""<!DOCTYPE html>{Environment.NewLine}<html><head></head><body><h1 id="header">Hello world</h1></body></html>"""
        ctx.Response.ContentType |> shouldEqual "text/html; charset=utf-8"
        ctx.Response.ContentLength |> shouldEqual(Nullable())
    }

// ---------------------------------------------------------------------------
// TryGet* extensions: StringValues conversion behaviour.
//
// Inputs are produced the way a real request produces them - a parsed query
// string, a parsed urlencoded form body and the request header dictionary -
// so the cases only cover values these getters can actually receive.
// ---------------------------------------------------------------------------

let private queryContext (wire: string) =
    let ctx = DefaultHttpContext()
    ctx.Request.QueryString <- QueryString("?" + wire)
    ctx

let private headerContext (values: string array) =
    let ctx = DefaultHttpContext()
    for value in values do
        ctx.Request.Headers.Append("X-Test", value)
    ctx

/// The implementations these extensions used to have, kept so the current ones
/// can be checked against the behaviour they replaced.
let private legacySingle (value: StringValues) = value |> string
let private legacyValues (value: StringValues) = value |> Seq.map string

let private isNotNull (value: string) = obj.ReferenceEquals(value, null) |> not

/// Key/value pairs exactly as they arrive on the wire. The urlencoded syntax is
/// the same for a query string and a form body, so both are driven from these.
let wireCases: seq<array<obj>> =
    seq {
        [| "q=a"; "a"; [| "a" |] |]
        [| "q=a&q=bb"; "a,bb"; [| "a"; "bb" |] |]
        [| "q=a&q=bb&q=ccc"; "a,bb,ccc"; [| "a"; "bb"; "ccc" |] |]
        [| "q="; ""; [| "" |] |]
        // joining skips blank entries, so the single-value getter drops the empty one
        [| "q=&q=a"; "a"; [| ""; "a" |] |]
        [| "q=a%20b"; "a b"; [| "a b" |] |]
        [| "q=%C3%A9"; "é"; [| "é" |] |]
    }

let headerCases: seq<array<obj>> =
    seq {
        [| [| "a" |]; "a"; [| "a" |] |]
        [| [| "a"; "bb" |]; "a,bb"; [| "a"; "bb" |] |]
        [|
            [| "gzip"; "deflate"; "br" |]
            "gzip,deflate,br"
            [| "gzip"; "deflate"; "br" |]
        |]
    }

[<Theory; MemberData(nameof wireCases)>]
let ``TryGetQueryValue and TryGetQueryValues convert a parsed query string``
    (wire: string, singleExpected: string, valuesExpected: string array)
    =
    let ctx = queryContext wire

    ctx.TryGetQueryValue "q" |> shouldEqual(Some singleExpected)
    ctx.TryGetQueryValues "q"
    |> Option.map Array.ofSeq
    |> shouldEqual(Some valuesExpected)

[<Theory; MemberData(nameof wireCases)>]
let ``TryGetFormValue and TryGetFormValues convert a parsed form body``
    (wire: string, singleExpected: string, valuesExpected: string array)
    =
    task {
        let ctx = createFormContext wire
        let! _ = ctx.Request.ReadFormAsync()

        ctx.TryGetFormValue "q" |> shouldEqual(Some singleExpected)
        ctx.TryGetFormValues "q"
        |> Option.map Array.ofSeq
        |> shouldEqual(Some valuesExpected)
    }

[<Theory; MemberData(nameof headerCases)>]
let ``TryGetHeaderValue and TryGetHeaderValues convert request headers``
    (values: string array, singleExpected: string, valuesExpected: string array)
    =
    let ctx = headerContext values

    ctx.TryGetHeaderValue "X-Test" |> shouldEqual(Some singleExpected)
    ctx.TryGetHeaderValues "X-Test"
    |> Option.map Array.ofSeq
    |> shouldEqual(Some valuesExpected)

[<Fact>]
let ``TryGet value extensions return None for a missing key`` () =
    task {
        let ctx = queryContext "other=1"
        ctx.TryGetQueryValue "q" |> shouldEqual None
        ctx.TryGetQueryValues "q" |> shouldEqual None
        ctx.TryGetHeaderValue "X-Test" |> shouldEqual None
        ctx.TryGetHeaderValues "X-Test" |> shouldEqual None

        let formCtx = createFormContext "other=1"
        let! _ = formCtx.Request.ReadFormAsync()
        formCtx.TryGetFormValue "q" |> shouldEqual None
        formCtx.TryGetFormValues "q" |> shouldEqual None
    }

[<Fact>]
let ``Appending an empty header value leaves the header absent`` () =
    // HeaderDictionary drops empty StringValues, so there is no "present but empty" header
    let ctx = headerContext [| "" |]

    ctx.Request.Headers.ContainsKey "X-Test" |> shouldEqual false
    ctx.TryGetHeaderValue "X-Test" |> shouldEqual None
    ctx.TryGetHeaderValues "X-Test" |> shouldEqual None

[<Theory; MemberData(nameof wireCases)>]
let ``TryGetQuery and TryGetForm match the previous implementation`` (wire: string, _: string, _: string array) =
    task {
        let ctx = queryContext wire
        let queryValue = ctx.Request.Query["q"]
        ctx.TryGetQueryValue "q" |> shouldEqual(Some(legacySingle queryValue))
        ctx.TryGetQueryValues "q"
        |> Option.map List.ofSeq
        |> shouldEqual(Some(legacyValues queryValue |> List.ofSeq))

        let formCtx = createFormContext wire
        let! _ = formCtx.Request.ReadFormAsync()
        let formValue = formCtx.Request.Form["q"]
        formCtx.TryGetFormValue "q" |> shouldEqual(Some(legacySingle formValue))
        formCtx.TryGetFormValues "q"
        |> Option.map List.ofSeq
        |> shouldEqual(Some(legacyValues formValue |> List.ofSeq))
    }

[<Theory; MemberData(nameof headerCases)>]
let ``TryGetHeader matches the previous implementation`` (values: string array, _: string, _: string array) =
    let ctx = headerContext values
    let headerValue = ctx.Request.Headers["X-Test"]

    ctx.TryGetHeaderValue "X-Test" |> shouldEqual(Some(legacySingle headerValue))
    ctx.TryGetHeaderValues "X-Test"
    |> Option.map List.ofSeq
    |> shouldEqual(Some(legacyValues headerValue |> List.ofSeq))

[<Theory; MemberData(nameof wireCases)>]
let ``Values from real collections are never null`` (wire: string, _: string, _: string array) =
    // TryGet*Values hands out the StringValues itself, which is only sound while
    // real query/form/header collections cannot contain null entries.
    task {
        let ctx = queryContext wire
        (ctx.TryGetQueryValues "q").Value |> Seq.forall isNotNull |> shouldEqual true

        let formCtx = createFormContext wire
        let! _ = formCtx.Request.ReadFormAsync()
        (formCtx.TryGetFormValues "q").Value |> Seq.forall isNotNull |> shouldEqual true

        let headerCtx = headerContext [| "a"; "bb" |]
        (headerCtx.TryGetHeaderValues "X-Test").Value
        |> Seq.forall isNotNull
        |> shouldEqual true
    }

[<Fact>]
let ``TryGetHeaderValues result can be enumerated more than once`` () =
    let ctx = headerContext [| "a"; "bb" |]

    let result = (ctx.TryGetHeaderValues "X-Test").Value

    result |> List.ofSeq |> shouldEqual [ "a"; "bb" ]
    result |> List.ofSeq |> shouldEqual [ "a"; "bb" ]

[<Fact>]
let ``TryGetHeaderValues result cannot be mutated into the header collection`` () =
    // The result is the StringValues itself rather than a copy, so it must not
    // expose a writable view onto the request headers.
    let ctx = headerContext [| "a"; "bb" |]

    let result = (ctx.TryGetHeaderValues "X-Test").Value

    result :? (string array) |> shouldEqual false
    match result with
    | :? IList<string> as list -> Assert.Throws<NotSupportedException>(fun () -> list[0] <- "mutated") |> ignore
    | _ -> ()

    ctx.Request.Headers["X-Test"] |> Array.ofSeq |> shouldEqual [| "a"; "bb" |]
    ctx.TryGetHeaderValues "X-Test"
    |> Option.map List.ofSeq
    |> shouldEqual(Some [ "a"; "bb" ])

let private contextWithServices (configure: IServiceCollection -> unit) =
    let services = ServiceCollection()
    services.AddOxpecker() |> ignore
    configure services
    let provider = services.BuildServiceProvider()
    let newContext () =
        // the way ASP.NET Core's DefaultHttpContextFactory sets up a request
        DefaultHttpContext(ServiceScopeFactory = provider.GetRequiredService<IServiceScopeFactory>())
    provider, newContext

[<Fact>]
let ``GetJsonSerializer and GetModelBinder return the registered singletons without creating request services`` () =
    let provider, newContext = contextWithServices ignore
    let ctx = newContext()

    ctx.GetJsonSerializer()
    |> shouldEqual(provider.GetRequiredService<IJsonSerializer>())
    ctx.GetModelBinder() |> shouldEqual(provider.GetRequiredService<IModelBinder>())
    ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IServiceProvidersFeature>()
    |> isNull
    |> shouldEqual true

[<Fact>]
let ``GetJsonSerializer returns a singleton registered after AddOxpecker`` () =
    let custom = SystemTextJsonSerializer()
    let _, newContext = contextWithServices(fun services -> services.AddSingleton<IJsonSerializer>(custom) |> ignore)

    newContext().GetJsonSerializer() |> shouldEqual(custom :> IJsonSerializer)

[<Fact>]
let ``GetJsonSerializer returns the request's instance of a scoped serializer`` () =
    let _, newContext =
        contextWithServices(fun services ->
            services.AddScoped<IJsonSerializer>(fun _ -> SystemTextJsonSerializer() :> IJsonSerializer)
            |> ignore)
    let ctx1 = newContext()
    let ctx2 = newContext()

    let serializer1 = ctx1.GetJsonSerializer()
    serializer1
    |> shouldEqual(ctx1.RequestServices.GetRequiredService<IJsonSerializer>())
    ctx1.GetJsonSerializer() |> shouldEqual serializer1
    Object.ReferenceEquals(ctx2.GetJsonSerializer(), serializer1)
    |> shouldEqual false

[<Fact>]
let ``GetModelBinder returns a new instance of a transient binder`` () =
    let _, newContext =
        contextWithServices(fun services ->
            services.AddTransient<IModelBinder>(fun _ -> ModelBinder() :> IModelBinder)
            |> ignore)
    let ctx = newContext()

    Object.ReferenceEquals(ctx.GetModelBinder(), ctx.GetModelBinder())
    |> shouldEqual false

[<Fact>]
let ``GetJsonSerializer uses RequestServices set on the context`` () =
    let _, newContext = contextWithServices ignore
    let custom = SystemTextJsonSerializer()
    let other = ServiceCollection().AddSingleton<IJsonSerializer>(custom).BuildServiceProvider()
    let ctx = newContext()
    ctx.RequestServices <- other

    ctx.GetJsonSerializer() |> shouldEqual(custom :> IJsonSerializer)

[<Fact>]
let ``GetJsonSerializer works without AddOxpecker`` () =
    let custom = SystemTextJsonSerializer()
    let provider = ServiceCollection().AddSingleton<IJsonSerializer>(custom).BuildServiceProvider()
    let ctx = DefaultHttpContext(ServiceScopeFactory = provider.GetRequiredService<IServiceScopeFactory>())

    ctx.GetJsonSerializer() |> shouldEqual(custom :> IJsonSerializer)

type QueryParsingModel = {
    Name: string | null
    Tags: string list
    Age: int option
    Items: StringCollectionModel array
}

let private bindQueryOutcome (readQueryFirst: bool) (queryString: string) =
    let ctx = createContext()
    ctx.Request.QueryString <- QueryString(queryString)
    if readQueryFirst then
        // Request.Query already built: BindQuery binds from the query collection
        ctx.Request.Query |> ignore
    try
        Ok(sprintf "%A" (ctx.BindQuery<QueryParsingModel>()))
    with ex ->
        Error(ex.GetType().FullName, ex.Message, (ex.InnerException |> Option.ofObj |> Option.map _.Message))

[<Theory>]
[<InlineData("")>]
[<InlineData("?")>]
[<InlineData("?Name=a")>]
[<InlineData("?name=a")>]
[<InlineData("?Name=a&name=b")>]
[<InlineData("?name=a&Name=b&NAME=c")>]
[<InlineData("?Tags=1&tags=2&TAGS=3&Tags=4")>]
[<InlineData("?Tags[0]=x&tags[1]=y&Tags[0]=z")>]
[<InlineData("?Name=%41%2B+b%20c&Tags=%E2%82%AC")>]
[<InlineData("?Na%6De=encoded-key&N+ame=space")>]
[<InlineData("?Name=%zz&Tags=%")>]
[<InlineData("?=x&Name&Tags&&Age=")>]
[<InlineData("?&&Name=a&&&Tags=b&")>]
[<InlineData("?Name=a=b&Tags==")>]
[<InlineData("?Age=42")>]
[<InlineData("?age=42&Age=43")>]
[<InlineData("?Age=abc")>]
[<InlineData("?Items[0].Tags[0]=a&items[0].tags[1]=b&Items[1].Tags=c")>]
[<InlineData("?Items.Tags=a&Items[x].Tags=b&Items[99999999999].Tags=c")>]
[<InlineData("?k1=1&k2=2&k3=3&k4=4&k5=5&k6=6&k7=7&k8=8&k9=9&Name=a&name=b")>]
[<InlineData("?k1=1&k2=2&k3=3&k4=4&k5=5&k6=6&k7=7&k8=8&k9=9&k10=10&Name=a&name=b")>]
[<InlineData("?Name=a&k1=1&k2=2&k3=3&k4=4&k5=5&k6=6&k7=7&k8=8&k9=9&name=b")>]
[<InlineData("?Name=a&k1=1&k2=2&k3=3&k4=4&k5=5&k6=6&k7=7&k8=8&k9=9&k10=10&name=b")>]
[<InlineData("?Name=a&name=b&k1=1&k2=2&k3=3&k4=4&k5=5&k6=6&k7=7&k8=8&k9=9&k10=10&NAME=c")>]
[<InlineData("?Tags=a&k1=1&k2=2&k3=3&k4=4&k5=5&k6=6&k7=7&k8=8&k9=9&k10=10&tags=b&TAGS=c")>]
[<InlineData("?k0=0&k1=1&k2=2&k3=3&k4=4&k5=5&k6=6&k7=7&k8=8&k9=9&k10=10&K3=33&Name=n&name=m&Tags=t")>]
let ``BindQuery binds the same from the query string as from Request.Query`` (queryString: string) =
    sprintf "%A" (bindQueryOutcome false queryString)
    |> shouldEqual(sprintf "%A" (bindQueryOutcome true queryString))

let private queryValueOutcome (readQueryFirst: bool) (queryString: string) (key: string) =
    let ctx = createContext()
    ctx.Request.QueryString <- QueryString(queryString)
    if readQueryFirst then
        // Request.Query already built: the lookups read the query collection
        ctx.Request.Query |> ignore
    let value = ctx.TryGetQueryValue key
    let values = ctx.TryGetQueryValues key |> Option.map List.ofSeq
    sprintf "%A %A" value values

[<Theory>]
[<InlineData("", "q")>]
[<InlineData("?", "q")>]
[<InlineData("?q=1", "q")>]
[<InlineData("?q=1", "Q")>]
[<InlineData("?Q=1&q=2&x=3&q=4", "q")>]
[<InlineData("?q", "q")>]
[<InlineData("?q=", "q")>]
[<InlineData("?q=a,b&q=c", "q")>]
[<InlineData("?=x&q=1", "")>]
[<InlineData("?a=1", "")>]
[<InlineData("?%71=enc&q+x=sp&q%20x=pct", "q")>]
[<InlineData("?%71=enc&q+x=sp&q%20x=pct", "q x")>]
[<InlineData("?q=%E2%82%AC+%2B%zz", "q")>]
[<InlineData("?&&q=1&&", "q")>]
[<InlineData("?q=1", "missing")>]
[<InlineData("?k%C3%A9y=1&K%C3%89Y=2", "kéy")>]
let ``TryGetQueryValue and TryGetQueryValues return the same as from Request.Query``
    (queryString: string, key: string)
    =
    queryValueOutcome false queryString key
    |> shouldEqual(queryValueOutcome true queryString key)

[<Fact>]
let ``TryGetQueryValue does not build Request.Query`` () =
    let ctx = createContext()
    ctx.Request.QueryString <- QueryString("?q=1")

    ctx.TryGetQueryValue "q" |> shouldEqual(Some "1")
    ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IQueryFeature>()
    |> isNull
    |> shouldEqual true

[<Fact>]
let ``TryGetQueryValue reads a query collection set on the request`` () =
    let ctx = createContext()
    ctx.Request.Query <- QueryCollection(Dictionary(dict [ "q", StringValues "set" ]))

    ctx.TryGetQueryValue "q" |> shouldEqual(Some "set")

type FormPerson = { Name: string; Age: int }

type private InvalidAntiforgery(error: exn) =
    interface Microsoft.AspNetCore.Antiforgery.IAntiforgeryValidationFeature with
        member _.IsValid = false
        member _.Error = error

[<Fact>]
let ``bindForm binds the model and leaves Request.Form readable`` () =
    task {
        let ctx = createFormContext "Name=don&Age=42"
        let mutable bound = None

        do!
            bindForm
                (fun (p: FormPerson) ->
                    bound <- Some p
                    text "ok")
                ctx

        bound |> shouldEqual(Some { Name = "don"; Age = 42 })
        ctx.Request.Form["Name"].ToString() |> shouldEqual "don"
    }

[<Fact>]
let ``bindForm returns a faulted task when the handler throws`` () =
    let ctx = createFormContext "Name=don&Age=42"

    let t = bindForm (fun (_: FormPerson) -> failwith "handler failed") ctx

    t.IsFaulted |> shouldEqual true
    (nonNull (nonNull t.Exception).InnerException).Message
    |> shouldEqual "handler failed"

[<Fact>]
let ``bindForm returns a task faulted with ModelBindException for an invalid model`` () =
    let ctx = createFormContext "Name=don&Age=notanumber"

    let t = bindForm (fun (_: FormPerson) -> text "ok") ctx

    t.IsFaulted |> shouldEqual true
    (nonNull t.Exception).InnerException :? ModelBindException |> shouldEqual true

[<Fact>]
let ``BindForm returns a task faulted with ModelBindException for an invalid model`` () =
    let ctx = createFormContext "Name=don&Age=notanumber"

    let t = ctx.BindForm<FormPerson>()

    t.IsFaulted |> shouldEqual true
    (nonNull t.Exception).InnerException :? ModelBindException |> shouldEqual true

[<Fact>]
let ``CSRF failure: BindForm throws synchronously, bindForm returns a faulted task`` () =
    let error = InvalidOperationException "csrf"
    let ctx = createFormContext "Name=don&Age=42"
    ctx.Features.Set<Microsoft.AspNetCore.Antiforgery.IAntiforgeryValidationFeature>(InvalidAntiforgery error)

    Assert.Throws<InvalidOperationException>(fun () -> ctx.BindForm<FormPerson>() |> ignore)
    |> shouldEqual error
    let t = bindForm (fun (_: FormPerson) -> text "ok") ctx
    t.IsFaulted |> shouldEqual true
    (nonNull t.Exception).InnerException |> nonNull |> shouldEqual(error :> exn)

[<Fact>]
let ``bindForm returns a cancelled task when reading the form is cancelled`` () =
    let ctx = createFormContext "Name=don&Age=42"
    use cts = new Threading.CancellationTokenSource()
    cts.Cancel()
    ctx.RequestAborted <- cts.Token

    let t = bindForm (fun (_: FormPerson) -> text "ok") ctx

    t.IsCanceled |> shouldEqual true
    ctx.BindForm<FormPerson>().IsCanceled |> shouldEqual true

let private jsonOutcome
    (read: HttpContext -> Threading.Tasks.Task<FormPerson>)
    (contentType: string)
    (body: byte array)
    =
    task {
        let ctx = DefaultHttpContext()
        ctx.Request.Body <- new MemoryStream(body)
        if not(isNull contentType) then
            ctx.Request.ContentType <- contentType
        try
            let! person = read ctx
            return sprintf "Ok %A" person
        with ex ->
            let rec chain (e: exn | null) =
                match e with
                | null -> []
                | e -> $"{e.GetType().FullName}: {e.Message}" :: chain e.InnerException
            return sprintf "Error %A" (chain ex)
    }

let jsonBodies: obj array seq =
    seq {
        let utf8 (s: string) = Encoding.UTF8.GetBytes s
        for contentType in
            [
                "application/json"
                "application/json; charset=utf-8"
                "Application/JSON; Charset=UTF-8"
                "application/json; charset=\"utf-8\""
                "application/problem+json"
                "application/json; charset=utf-16"
                "application/json; charset=nope"
                "text/plain"
                "not a content type"
                null
            ] do
            for body in
                [
                    utf8 """{"name":"don","age":42}"""
                    Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (utf8 """{"name":"bom","age":1}""")
                    utf8 "null"
                    utf8 ""
                    utf8 """{"name":"don","age":42} trailing"""
                    utf8 """{"name":"don","age":"x"}"""
                    Encoding.Unicode.GetBytes """{"name":"utf16","age":2}"""
                ] do
                [| box contentType; box body |]
    }

[<Theory>]
[<MemberData(nameof jsonBodies)>]
let ``SystemTextJsonSerializer.Deserialize gives what ReadFromJsonAsync gives``
    (contentType: string, body: byte array)
    =
    task {
        let options = Json.JsonSerializerOptions(Json.JsonSerializerDefaults.Web)
        let serializer = SystemTextJsonSerializer(options) :> IJsonSerializer
        let reference (ctx: HttpContext) =
            task {
                match! ctx.Request.ReadFromJsonAsync<FormPerson>(options, ctx.RequestAborted) with
                | null -> return Unchecked.defaultof<_>
                | v -> return v
            }

        let! actual = jsonOutcome serializer.Deserialize<FormPerson> contentType body
        let! expected = jsonOutcome reference contentType body

        actual |> shouldEqual expected
    }

let private createJsonContext (body: string) (serializer: IJsonSerializer) =
    let ctx = DefaultHttpContext()
    let services = ServiceCollection()
    services.AddSingleton<IJsonSerializer>(serializer) |> ignore
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes body)
    ctx.Request.ContentType <- "application/json"
    ctx

type private FixedTaskSerializer(result: Threading.Tasks.Task<FormPerson>) =
    interface IJsonSerializer with
        member _.Serialize(_, _, _) = Threading.Tasks.Task.CompletedTask
        member _.SerializePart(_, _, _) = Threading.Tasks.Task.CompletedTask
        member _.Deserialize<'T>(_) = box result :?> Threading.Tasks.Task<'T>

[<Fact>]
let ``bindJson binds the model with the default serializer`` () =
    task {
        let ctx = createJsonContext """{"name":"don","age":42}""" (SystemTextJsonSerializer())
        let mutable bound = None

        do!
            bindJson
                (fun (p: FormPerson) ->
                    bound <- Some p
                    text "ok")
                ctx

        bound |> shouldEqual(Some { Name = "don"; Age = 42 })
    }

[<Fact>]
let ``bindJson returns a faulted task when the handler throws`` () =
    let ctx = createJsonContext """{"name":"don","age":42}""" (SystemTextJsonSerializer())

    let t = bindJson (fun (_: FormPerson) -> failwith "handler failed") ctx

    t.IsFaulted |> shouldEqual true
    t.Exception.InnerException.Message |> shouldEqual "handler failed"

[<Fact>]
let ``bindJson and BindJson give a task faulted with ModelBindException for invalid JSON`` () =
    task {
        let t =
            bindJson
                (fun (_: FormPerson) -> text "ok")
                (createJsonContext """{"name":"don","age":"x"}""" (SystemTextJsonSerializer()))
        let! _ = Threading.Tasks.Task.WhenAny t
        t.IsFaulted |> shouldEqual true
        t.Exception.InnerException :? ModelBindException |> shouldEqual true

        let t2 = (createJsonContext """{"name":"don","age":"x"}""" (SystemTextJsonSerializer())).BindJson<FormPerson>()
        let! _ = Threading.Tasks.Task.WhenAny t2
        t2.IsFaulted |> shouldEqual true
        t2.Exception.InnerException :? ModelBindException |> shouldEqual true
    }

[<Fact>]
let ``bindJson and BindJson return a cancelled task when the request is aborted`` () =
    task {
        use cts = new Threading.CancellationTokenSource()
        cts.Cancel()
        let ctx = createJsonContext """{"name":"don","age":42}""" (SystemTextJsonSerializer())
        ctx.RequestAborted <- cts.Token

        let t = bindJson (fun (_: FormPerson) -> text "ok") ctx
        let! _ = Threading.Tasks.Task.WhenAny t
        t.IsCanceled |> shouldEqual true
        let t2 = ctx.BindJson<FormPerson>()
        let! _ = Threading.Tasks.Task.WhenAny t2
        t2.IsCanceled |> shouldEqual true
    }

[<Fact>]
let ``BindJson hands out another serializer's completed task as is`` () =
    let result = Threading.Tasks.Task.FromResult { Name = "fixed"; Age = 1 }
    let ctx = createJsonContext "" (FixedTaskSerializer result)

    obj.ReferenceEquals(ctx.BindJson<FormPerson>(), result) |> shouldEqual true

[<Fact>]
let ``BindJson wraps another serializer's failure in ModelBindException`` () =
    task {
        let failed = Threading.Tasks.Task.FromException<FormPerson>(InvalidOperationException "bad")
        let ctx = createJsonContext "" (FixedTaskSerializer failed)

        let t = ctx.BindJson<FormPerson>()
        let! _ = Threading.Tasks.Task.WhenAny t
        t.IsFaulted |> shouldEqual true
        t.Exception.InnerException :? ModelBindException |> shouldEqual true
        t.Exception.InnerException.InnerException.Message |> shouldEqual "bad"
    }

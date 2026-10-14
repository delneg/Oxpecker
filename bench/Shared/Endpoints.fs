module OxpBench.Endpoints

open System
open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.ViewEngine

[<CLIMutable>]
type Message = { message: string }

type Person = { Name: string; Age: int }

type User = { Name: string; Age: int; Created: DateTime; Height: float }

let users = [| for i in 1..6 -> { Name = $"User{i}"; Age = 20 + i; Created = DateTime(2026, 1, 1); Height = 170.0 + float i } |]

let page (name: string) =
    html() {
        head() { title() { "Bench" } }
        body() {
            h1(class' = "title") { "Hello, " + name }
            ul() {
                for u in users do
                    li(id = u.Name) { u.Name }
            }
        }
    }

let headerQuery: EndpointHandler =
    fun ctx ->
        let h = ctx.TryGetHeaderValue "X-Test" |> Option.defaultValue "none"
        let q = ctx.TryGetQueryValue "q" |> Option.defaultValue "none"
        ctx.WriteText(if h.Length + q.Length > 0 then "ok" else "empty")

let endpoints = [
    GET [
        route "/plaintext" <| text "Hello, World!"
        route "/json" <| json { message = "Hello, World!" }
        route "/json-users" <| json users
        routef "/user/{%s}/{%i}" (fun (name: string) (id: int) -> text "User received")
        routef "/userctx/{%s}/{%i}" (fun (name: string) (id: int) (ctx: HttpContext) -> ctx.WriteText "User received")
        route "/headers" headerQuery
        route "/composed" (setStatusCode 200 >=> setHttpHeader "X-Bench" "1" >=> text "composed")
        route "/html" <| htmlView (page "World")
        route "/nocache" (noResponseCaching >=> text "nocache")
        route "/precond" (validatePreconditions (Some(createETag "v1")) (Some(DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))) >=> text "precond")
    ]
    GET [ route "/bind-query" <| bindQuery<Person>(fun p -> text p.Name) ]
    POST [
        route "/bind-form" <| bindForm<Person>(fun p -> text p.Name)
        route "/bind-json" <| bindJson<Person>(fun p -> text p.Name)
    ]
    subRoutef "/org/{%s}" (fun (org: string) -> [ GET [ route "/info" (text "org info"); route "/other" (text "other") ] ])
    subRoute "/api" [
        GET [ route "/users" <| text "Users received" ]
    ]
]


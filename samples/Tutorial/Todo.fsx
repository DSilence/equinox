﻿// Compile Tutorial.fsproj by either a) right-clicking or b) typing
// dotnet build samples/Tutorial before attempting to send this to FSI with Alt-Enter
#if VISUALSTUDIO
#r "netstandard"
#endif
#I "bin/Debug/netstandard2.0/"
#r "Serilog.dll"
#r "Serilog.Sinks.Console.dll"
#r "Newtonsoft.Json.dll"
#r "TypeShape.dll"
#r "Equinox.dll"
#r "FsCodec.dll"
#r "FsCodec.NewtonsoftJson.dll"
#r "FSharp.Control.AsyncSeq.dll"
#r "Microsoft.Azure.DocumentDb.Core.dll"
#r "Equinox.Cosmos.dll"

open Equinox.Cosmos
open System

(* NB It's recommended to look at Favorites.fsx first as it establishes the groundwork
   This tutorial stresses different aspects *)

type Todo = { id: int; order: int; title: string; completed: bool }
type DeletedInfo = { id: int }
type CompactedInfo = { items: Todo[] }
type Event =
    | Added     of Todo
    | Updated   of Todo
    | Deleted   of DeletedInfo
    | Cleared
    | Compacted of CompactedInfo
    interface TypeShape.UnionContract.IUnionContract
let codec = FsCodec.NewtonsoftJson.Codec.Create<Event>()

type State = { items : Todo list; nextId : int }
let initial = { items = []; nextId = 0 }
let evolve s (e : Event) = 
    match e with
    | Added item -> { s with items = item :: s.items; nextId = s.nextId + 1 }
    | Updated value -> { s with items = s.items |> List.map (function { id = id } when id = value.id -> value | item -> item) }
    | Deleted { id=id } -> { s with items = s.items |> List.filter (fun x -> x.id <> id) }
    | Cleared -> { s with items = [] }
    | Compacted { items=items } -> { s with items = List.ofArray items }
let fold state = Seq.fold evolve state
let isOrigin = function Cleared | Compacted _ -> true | _ -> false
let compact state = Compacted { items = Array.ofList state.items }

type Command = Add of Todo | Update of Todo | Delete of id: int | Clear
let interpret c (state : State) =
    match c with
    | Add value -> [Added { value with id = state.nextId }]
    | Update value ->
        match state.items |> List.tryFind (function { id = id } -> id = value.id) with
        | Some current when current <> value -> [Updated value]
        | _ -> []
    | Delete id -> if state.items |> List.exists (fun x -> x.id = id) then [Deleted { id=id }] else []
    | Clear -> if state.items |> List.isEmpty then [] else [Cleared]

type Service(log, resolveStream, ?maxAttempts) =
    let (|AggregateId|) (id : string) = Equinox.AggregateId("Todos", id)
    let (|Stream|) (AggregateId id) = Equinox.Stream(log, resolveStream id, defaultArg maxAttempts 3)
    let execute (Stream stream) command : Async<unit> =
        stream.Transact(interpret command)
    let handle (Stream stream) command : Async<Todo list> =
        stream.Transact(fun state ->
            let ctx = Equinox.Accumulator(fold, state)
            ctx.Execute (interpret command)
            ctx.State.items,ctx.Accumulated)
    let query (Stream stream) (projection : State -> 't) : Async<'t> =
        stream.Query projection
    member __.List clientId : Async<Todo seq> =
        query clientId (fun s -> s.items |> Seq.ofList)
    member __.TryGet(clientId, id) =
        query clientId (fun x -> x.items |> List.tryFind (fun x -> x.id = id))
    member __.Execute(clientId, command) : Async<unit> =
        execute clientId command
    member __.Create(clientId, template: Todo) : Async<Todo> = async {
        let! state' = handle clientId (Add template)
        return List.head state' }
    member __.Patch(clientId, item: Todo) : Async<Todo> = async {
        let! state' = handle clientId (Update item)
        return List.find (fun x -> x.id = item.id) state' }
    member __.Clear clientId : Async<unit> =
        execute clientId Clear

(*
 * EXERCISE THE SERVICE
 *)

let initialState = initial
//val initialState : State = {items = [];
//                            nextId = 0;}

let oneItem = fold initialState [Added { id = 0; order = 0; title = "Feed cat"; completed = false }]
//val oneItem : State = {items = [{id = 0;
//                                 order = 0;
//                                 title = "Feed cat";
//                                 completed = false;}];
//                       nextId = 1;}

fold oneItem [Cleared]
//val it : State = {items = [];
//                  nextId = 1;}

open Serilog
let log = LoggerConfiguration().WriteTo.Console().CreateLogger()

module Store =
    let read key = System.Environment.GetEnvironmentVariable key |> Option.ofObj |> Option.get

    let connector = Connector(requestTimeout=TimeSpan.FromSeconds 5., maxRetryAttemptsOnThrottledRequests=2, maxRetryWaitTimeInSeconds=5, log=log)
    let conn = connector.Connect("equinox-tutorial", Discovery.FromConnectionString (read "EQUINOX_COSMOS_CONNECTION")) |> Async.RunSynchronously
    let gateway = Gateway(conn, BatchingPolicy())

    let store = Context(gateway, read "EQUINOX_COSMOS_DATABASE", read "EQUINOX_COSMOS_CONTAINER")
    let cache = Caching.Cache("equinox-tutorial", 20)
    let cacheStrategy = CachingStrategy.SlidingWindow (cache, TimeSpan.FromMinutes 20.)

module TodosCategory = 
    let access = AccessStrategy.Snapshot (isOrigin,compact)
    let resolve = Resolver(Store.store, codec, fold, initial, Store.cacheStrategy, access=access).Resolve

let service = Service(log, TodosCategory.resolve)

let client = "ClientJ"
let item = { id = 0; order = 0; title = "Feed cat"; completed = false }
service.Create(client, item) |> Async.RunSynchronously
//val it : Todo = {id = 0;
//                 order = 0;
//                 title = "Feed cat";
//                 completed = false;}
service.List(client) |> Async.RunSynchronously
//val it : seq<Todo> = [{id = 0;
//                       order = 0;
//                       title = "Feed cat";
//                       completed = false;}]
service.Execute(client, Clear) |> Async.RunSynchronously
//val it : unit = ()

service.TryGet(client,42) |> Async.RunSynchronously
//val it : Todo option = None

let item2 = { id = 3; order = 0; title = "Feed dog"; completed = false }
service.Create(client, item2) |> Async.RunSynchronously
service.TryGet(client, 3) |> Async.RunSynchronously
//val it : Todo option = Some {id = 3;
//                             order = 0;
//                             title = "Feed dog";
//                             completed = false;}

let itemH = { id = 1; order = 0; title = "Feed horse"; completed = false }
service.Patch(client, itemH) |> Async.RunSynchronously
//[05:49:33 INF] EqxCosmos Tip 302 116ms rc=1
//Updated {id = 1;
//         order = 0;
//         title = "Feed horse";
//         completed = false;}Updated {id = 1;
//         order = 0;
//         title = "Feed horse";
//         completed = false;}[05:49:33 INF] EqxCosmos Sync 1+1 534ms rc=14.1
//val it : Todo = {id = 1;
//                 order = 0;
//                 title = "Feed horse";
//                 completed = false;}
service.Execute(client, Delete 1) |> Async.RunSynchronously 
//[05:47:18 INF] EqxCosmos Tip 302 224ms rc=1
//Deleted 1[05:47:19 INF] EqxCosmos Sync 1+1 230ms rc=13.91
//val it : unit = ()
service.List(client) |> Async.RunSynchronously
//[05:47:22 INF] EqxCosmos Tip 302 119ms rc=1
//val it : seq<Todo> = [] 
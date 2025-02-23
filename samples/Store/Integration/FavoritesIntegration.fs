﻿module Samples.Store.Integration.FavoritesIntegration

open Equinox
open Equinox.Cosmos.Integration
open Swensen.Unquote
open Xunit

#nowarn "1182" // From hereon in, we may have some 'unused' privates (the tests)

let fold, initial = Domain.Favorites.Folds.fold, Domain.Favorites.Folds.initial
let snapshot = Domain.Favorites.Folds.isOrigin, Domain.Favorites.Folds.compact

let createMemoryStore () =
    new MemoryStore.VolatileStore()
let createServiceMemory log store =
    Backend.Favorites.Service(log, MemoryStore.Resolver(store, fold, initial).Resolve)

let codec = Domain.Favorites.Events.codec
let createServiceGes gateway log =
    let resolveStream = EventStore.Resolver(gateway, codec, fold, initial, access = EventStore.AccessStrategy.RollingSnapshots snapshot).Resolve
    Backend.Favorites.Service(log, resolveStream)

let createServiceCosmos gateway log =
    let resolveStream = Cosmos.Resolver(gateway, codec, fold, initial, Cosmos.CachingStrategy.NoCaching, Cosmos.AccessStrategy.Snapshot snapshot).Resolve
    Backend.Favorites.Service(log, resolveStream)

let createServiceCosmosRollingUnfolds gateway log =
    let access = Cosmos.AccessStrategy.RollingUnfolds(Domain.Favorites.Folds.isOrigin, Domain.Favorites.Folds.transmute)
    let resolveStream = Cosmos.Resolver(gateway, codec, fold, initial, Cosmos.CachingStrategy.NoCaching, access).Resolve
    Backend.Favorites.Service(log, resolveStream)

type Tests(testOutputHelper) =
    let testOutput = TestOutputAdapter testOutputHelper
    let createLog () = createLogger testOutput

    let act (service : Backend.Favorites.Service) (clientId, command) = async {
        do! service.Execute(clientId, command)
        let! items = service.List clientId

        match command with
        | Domain.Favorites.Favorite (_,skuIds) ->
            test <@ skuIds |> List.forall (fun skuId -> items |> Array.exists (function { skuId = itemSkuId} -> itemSkuId = skuId)) @>
        | _ ->
            test <@ Array.isEmpty items @> }

    [<AutoData>]
    let ``Can roundtrip in Memory, correctly folding the events`` args = Async.RunSynchronously <| async {
        let store = createMemoryStore ()
        let service = let log = createLog () in createServiceMemory log store
        do! act service args
    }

    [<AutoData(SkipIfRequestedViaEnvironmentVariable="EQUINOX_INTEGRATION_SKIP_EVENTSTORE")>]
    let ``Can roundtrip against EventStore, correctly folding the events`` args = Async.RunSynchronously <| async {
        let log = createLog ()
        let! conn = connectToLocalEventStoreNode log
        let gateway = createGesGateway conn defaultBatchSize
        let service = createServiceGes gateway log
        do! act service args
    }

    [<AutoData(SkipIfRequestedViaEnvironmentVariable="EQUINOX_INTEGRATION_SKIP_COSMOS")>]
    let ``Can roundtrip against Cosmos, correctly folding the events`` args = Async.RunSynchronously <| async {
        let log = createLog ()
        let! conn = connectToSpecifiedCosmosOrSimulator log
        let gateway = createCosmosContext conn defaultBatchSize
        let service = createServiceCosmos gateway log
        do! act service args
    }
    
    [<AutoData(SkipIfRequestedViaEnvironmentVariable="EQUINOX_INTEGRATION_SKIP_COSMOS")>]
    let ``Can roundtrip against Cosmos, correctly folding the events with rolling unfolds`` args = Async.RunSynchronously <| async {
        let log = createLog ()
        let! conn = connectToSpecifiedCosmosOrSimulator log
        let gateway = createCosmosContext conn defaultBatchSize
        let service = createServiceCosmosRollingUnfolds gateway log
        do! act service args
    }
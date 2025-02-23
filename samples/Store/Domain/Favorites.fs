﻿module Domain.Favorites

// NB - these schemas reflect the actual storage formats and hence need to be versioned with care
module Events =
    type Favorited =                            { date: System.DateTimeOffset; skuId: SkuId }
    type Unfavorited =                          { skuId: SkuId }
    module Compaction =
        type Compacted =                        { net: Favorited[] }

    type Event =
        | Compacted                             of Compaction.Compacted
        | Favorited                             of Favorited
        | Unfavorited                           of Unfavorited
        interface TypeShape.UnionContract.IUnionContract
    let codec = FsCodec.NewtonsoftJson.Codec.Create<Event>()

module Folds =
    type State = Events.Favorited []

    type private InternalState(input: State) =
        let dict = new System.Collections.Generic.Dictionary<SkuId, Events.Favorited>()
        let favorite (e : Events.Favorited) =   dict.[e.skuId] <- e
        let favoriteAll (xs: Events.Favorited seq) = for x in xs do favorite x
        do favoriteAll input
        member __.ReplaceAllWith xs =           dict.Clear(); favoriteAll xs
        member __.Favorite(e : Events.Favorited) =  favorite e
        member __.Unfavorite id =               dict.Remove id |> ignore
        member __.AsState() =                   Seq.toArray dict.Values

    let initial : State = [||]
    let private evolve (s: InternalState) = function
        | Events.Compacted { net = net } ->     s.ReplaceAllWith net
        | Events.Favorited e ->                 s.Favorite e
        | Events.Unfavorited { skuId = id } ->  s.Unfavorite id
    let fold (state: State) (events: seq<Events.Event>) : State =
        let s = InternalState state
        for e in events do evolve s e
        s.AsState()
    let isOrigin = function Events.Compacted _ -> true | _ -> false
    let compact state = Events.Compacted { net = state }
    /// This transmute impl a) removes events - we're not interested in storing the events b) packs the post-state into a Compacted unfold-event
    let transmute _events state = [],[compact state]

type Command =
    | Favorite      of date : System.DateTimeOffset * skuIds : SkuId list
    | Unfavorite    of skuId : SkuId

module Commands =
    let interpret command (state : Folds.State) =
        let doesntHave skuId = state |> Array.exists (fun x -> x.skuId = skuId) |> not
        match command with
        | Favorite (date = date; skuIds = skuIds) ->
            [ for skuId in Seq.distinct skuIds do
                if doesntHave skuId then
                    yield Events.Favorited { date = date; skuId = skuId } ]
        | Unfavorite skuId ->
            if doesntHave skuId then [] else
            [ Events.Unfavorited { skuId = skuId } ]
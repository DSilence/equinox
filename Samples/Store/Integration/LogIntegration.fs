﻿module Samples.Store.Integration.LogIntegration

open Foldunk.EventStore
open Swensen.Unquote
open System

module Interop =
    [<NoEquality; NoComparison>]
    type FlatMetric = { action: string; stream: string; interval: StopwatchInterval; bytes: int; count: int; batches: int option } with
        override __.ToString() = sprintf "%s-Stream=%s %s-Elapsed=%O" __.action __.stream __.action __.interval.Elapsed
    let flatten (evt : Log.Event) : FlatMetric =
        let action, metric, batches =
            match evt with
            | Log.WriteSuccess m -> "AppendToStreamAsync", m, None
            | Log.WriteConflict m -> "AppendToStreamAsync", m, None
            | Log.Slice (Direction.Forward,m) -> "ReadStreamEventsForwardAsync", m, None
            | Log.Slice (Direction.Backward,m) -> "ReadStreamEventsBackwardAsync", m, None
            | Log.Batch (Direction.Forward,c,m) -> "LoadF", m, Some c
            | Log.Batch (Direction.Backward,c,m) -> "LoadB", m, Some c
        { action = action; stream = metric.stream; interval = metric.interval; bytes = metric.bytes; count = metric.count; batches = batches }

type SerilogMetricsExtractor(emit : string -> unit) =
    let render template =
        let formatter = Serilog.Formatting.Display.MessageTemplateTextFormatter(template, null)
        fun logEvent ->
            use writer = new System.IO.StringWriter()
            formatter.Format(logEvent, writer)
            writer |> string
    let renderFull = render "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] {Message} {Properties} {NewLine}{Exception}"
    let renderSummary = render "{Message} {Properties}"
    let emitEvent (logEvent : Serilog.Events.LogEvent) =
        logEvent |> renderFull |> System.Diagnostics.Trace.Write
        logEvent |> renderSummary |> emit
    let (|SerilogScalar|_|) : Serilog.Events.LogEventPropertyValue -> obj option = function
        | (:? Serilog.Events.ScalarValue as x) -> Some x.Value
        | _ -> None
    let (|EsEvent|_|) (logEvent : Serilog.Events.LogEvent) : (string * Foldunk.EventStore.Log.Event) option =
        logEvent.Properties |> Seq.tryPick (function KeyValue (k, SerilogScalar (:? Foldunk.EventStore.Log.Event as m)) -> Some (k,m) | _ -> None)
    let handleLogEvent = function
        | EsEvent (name, evt) as logEvent ->
            let flat = Interop.flatten evt
            let renderedMetrics = sprintf "%s-Duration=%O" flat.action flat.interval.Elapsed |> Serilog.Events.ScalarValue
            // Serilog provides lots of ways of configuring custom rendering -  solely tweaking the rendering is doable using the configuration syntax
            // (the goal here is to illustrate how a given value can be extracted (as required in some cases) and/or stubbed yet retain the rest of the message)
            // Other example approaches:
            // 1. logEvent.RemovePropertyIfExists name
            // 2. let rendered = logEvent.RenderMessage() in rendered.Replace("{esEvt} ","")
            logEvent.AddOrUpdateProperty(Serilog.Events.LogEventProperty(name, renderedMetrics))
            emitEvent logEvent
        | logEvent -> emitEvent logEvent
    interface Serilog.Core.ILogEventSink with member __.Emit logEvent = handleLogEvent logEvent

let createLoggerWithMetricsExtraction emit =
    let capture = SerilogMetricsExtractor emit
    createLogger capture, capture

#nowarn "1182" // From hereon in, we may have some 'unused' privates (the tests)

type Tests() =
    // Protip: Debug this test to view standard metrics rendering
    [<AutoData>]
    let ``Can roundtrip against EventStore, hooking, extracting and substituting metrics in the logging information`` context cartId skuId = Async.RunSynchronously <| async {
        let buffer = ResizeArray<string>()
        let batchSize = defaultBatchSize
        let (log,capture) = createLoggerWithMetricsExtraction buffer.Add
        let! conn = connectToLocalEventStoreNode log
        let service = CartIntegration.createServiceGes conn batchSize

        let itemCount, cartId = batchSize / 2 + 1, cartId ()
        do! CartIntegration.addAndThenRemoveItemsManyTimesExceptTheLastOne context cartId skuId log service itemCount

        let! state = service.Read log cartId
        test <@ itemCount = match state with { items = [{ quantity = quantity }] } -> quantity | _ -> failwith "nope" @>

        // Even though we've gone over a page, we only need a single read to read the state (plus the one from the execute)
        let contains (s : string) (x : string) = x.IndexOf s <> -1
        test <@ let reads = buffer |> Seq.filter (fun s -> s |> contains "ReadStreamEventsBackwardAsync-Duration")
                2 = Seq.length reads
                && not (obj.ReferenceEquals(capture, null)) @>
    }
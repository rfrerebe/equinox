﻿/// Implements an in-memory store. This fulfils two goals:
/// 1. Acts as A target for integration testing allowing end-to-end processing of a decision flow in an efficient test
/// 2. Illustrates a minimal implemention of the Foldunk Storage interface interconnects for the purpose of writing Store connectors
namespace Foldunk.MemoryStore

open Foldunk
open Serilog

/// Equivalent to GetEventStore in purpose; signals a conflict has been detected and reprocessing of the decision will be necessary
exception private WrongVersionException of streamName: string * expected: int * value: obj

/// Internal result used to reflect the outcome of syncing with the entry in the inner ConcurrentDictionary
[<NoEquality; NoComparison>]
type ConcurrentDictionarySyncResult<'t> = Written of 't | Conflict of int

/// Response type for ConcurrentArrayStore.TrySync to communicate the outcome and updated state of a stream
[<NoEquality; NoComparison>]
type ConcurrentArraySyncResult<'t> = Written of 't | Conflict of 't

// Maintains a dictionary of boxed typed arrays, raising exceptions if an attempt to extract a value encounters a mismatched type
type private ConcurrentArrayStore() =
    let streams = System.Collections.Concurrent.ConcurrentDictionary<string,obj>()
    let mkBadValueException (log : ILogger) streamName (value : obj) =
        let desc = match value with null -> "null" | v -> v.GetType().FullName
        let ex : exn = invalidOp (sprintf "Could not read stream %s, value was a: %s" streamName desc)
        log.Error<_,_>(ex, "Read Bad Value {StreamName} {Value}", streamName, value)
        ex
    let mkWrongVersionException (log : ILogger) streamName (expected : int) (value: obj) =
        let ex : exn = WrongVersionException (streamName, expected, value)
        log.Warning<_,_,_>(ex, "Unexpected Stored Value {StreamName} {Expected} {Value}", streamName, expected, value)
        ex
    member private __.Unpack<'event> log streamName (x : obj): 'event array =
        match x with
        | :? ('event array) as value -> value
        | value -> raise (mkBadValueException log streamName value)
    member private __.Pack (events : 'event seq) : obj =
        Array.ofSeq events |> box

    /// Loads state from a given stream
    member __.TryLoad streamName log =
        match streams.TryGetValue streamName with
        | false, _ -> None
        | true, packed -> __.Unpack log streamName packed |> Some

    /// Attempts a sychronization operation - yields conflicting value if sync function decides there is a conflict
    member __.TrySync streamName (log : Serilog.ILogger)  (trySyncValue : 'events array -> ConcurrentDictionarySyncResult<'event seq>) (events: 'event seq)
        : ConcurrentArraySyncResult<'event array> =
        let seedStream _streamName = __.Pack events
        let updatePackedValue streamName (packedCurrentValue : obj) =
            let currentValue = __.Unpack log streamName packedCurrentValue
            match trySyncValue currentValue with
            | ConcurrentDictionarySyncResult.Conflict expectedVersion -> raise (mkWrongVersionException log streamName expectedVersion packedCurrentValue)
            | ConcurrentDictionarySyncResult.Written value -> __.Pack value
        try
            let boxedSyncedValue = streams.AddOrUpdate(streamName, seedStream, updatePackedValue)
            ConcurrentArraySyncResult.Written (unbox boxedSyncedValue)
        with WrongVersionException(_, _, conflictingValue) ->
            ConcurrentArraySyncResult.Conflict (unbox conflictingValue)

/// Internal implementation detail of MemoryStreamStore
module private MemoryStreamStreamState =
    let private streamTokenOfIndex (streamVersion : int) : Storage.StreamToken =
        { value = box streamVersion }
    /// Represent a stream known to be empty
    let ofEmpty () = streamTokenOfIndex -1, None, []
    let tokenOfArray (value: 'event array) = Array.length value - 1 |> streamTokenOfIndex
    /// Represent a known array of events (without a known folded State)
    let ofEventArray (events: 'event array) = tokenOfArray events, None, List.ofArray events
    /// Represent a known array of Events together with the associated state
    let ofEventArrayAndKnownState (events: 'event array) (state: 'state) = tokenOfArray events, Some state, []

/// Represents the state of a set of streams in a style consistent withe the concrete Store types - no constraints on memory consumption (but also no persistence!).
type MemoryStreamStore() =
    let store = ConcurrentArrayStore()
    member __.Load streamName (log : Serilog.ILogger) = async {
        match store.TryLoad<'event> streamName log with
        | None -> return MemoryStreamStreamState.ofEmpty ()
        | Some events -> return MemoryStreamStreamState.ofEventArray events }
    member __.TrySync streamName (log : Serilog.ILogger)  (token, snapshotState) (events: 'event list, proposedState) = async {
        let trySyncValue currentValue =
            if Array.length currentValue <> unbox token + 1 then ConcurrentDictionarySyncResult.Conflict (unbox token)
            else ConcurrentDictionarySyncResult.Written (Seq.append currentValue events)
        match store.TrySync streamName (log : Serilog.ILogger) trySyncValue events with
        | ConcurrentArraySyncResult.Conflict conflictingEvents ->
            let resync = async {
                let version = MemoryStreamStreamState.tokenOfArray conflictingEvents
                let successorEvents = conflictingEvents |> Seq.skip (unbox token + 1) |> List.ofSeq
                return Storage.StreamState.ofTokenSnapshotAndEvents version snapshotState successorEvents }
            return Storage.SyncResult.Conflict resync
        | ConcurrentArraySyncResult.Written events -> return Storage.SyncResult.Written <| MemoryStreamStreamState.ofEventArrayAndKnownState events proposedState }

/// Represents a specific stream in a MemoryStreamStore
type MemoryStream<'event, 'state>(store : MemoryStreamStore, streamName) =
    interface IStream<'event, 'state> with
        member __.Load log = store.Load streamName log
        member __.TrySync (log: ILogger) (token: Storage.StreamToken, originState: 'state) (events: 'event list, state': 'state) = 
            store.TrySync streamName log (token, originState) (events, state')
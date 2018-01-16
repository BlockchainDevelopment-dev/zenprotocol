module Consensus.UtxoSet

open Consensus.Types

type InputStatus =
    | Spent of Output // TODO: this is a hack for undo blocks, we should not keep the output, we need to have access to output repository in the future
    | Unspent of Output

type T = Map<Outpoint, InputStatus>

let create () =
    Map.empty

let handleTransaction txHash tx (set:T) =
    let folder state input =
        match Map.find input state with
        | Unspent output -> Map.add input (Spent output) state
        | _ -> failwith "Expected output to be unspent"

    let utxos = List.fold folder set tx.inputs
    let outputsWithIndex = List.mapi (fun i output -> (uint32 i,output)) tx.outputs

    List.fold (fun state (index,output) ->
        let outpoint = {txHash=txHash;index=index;}
        Map.add outpoint (Unspent output) state) utxos outputsWithIndex

let isSomeSpent outpoints set =
    List.fold (fun state outpoint ->
        match state with
        | true -> true
        | false ->
            match Map.tryFind outpoint set with
            | Some (Spent _) -> true
            | _ -> false) false outpoints

let getUtxos outpoints set =
    List.foldBack (fun outpoint state ->
        match state with
            | None -> None
            | Some list ->
                match Map.tryFind outpoint set with
                | None
                | Some (Spent _) ->
                    None
                | Some (Unspent output) -> Some (output :: list))
        outpoints (Some [])

let undoBlock block utxoSet =

    // unmark any spent output
    let utxoSet  =
        List.map (fun tx -> tx.inputs) block.transactions
        |> List.concat
        |> List.fold (fun utxoSet input ->
            match Map.find input utxoSet with
            | Spent output -> Map.add input (Unspent output) utxoSet
            | _ -> failwith "Expected output to be spent"
            ) utxoSet

    // remove all outputs
    List.fold (fun utxoSet tx ->
        let txHash = Transaction.hash tx

        tx.outputs
        |> List.mapi (fun i _ -> i)
        |> List.fold (fun utxoSet i ->
            let outpoint = {txHash=txHash; index=uint32 i}

            Map.remove outpoint utxoSet) utxoSet) utxoSet block.transactions
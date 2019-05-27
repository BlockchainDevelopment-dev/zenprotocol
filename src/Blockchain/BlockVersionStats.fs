module Blockchain.BlockVersionStats

open Consensus
open Types
open Checked

type T = Map<uint32, uint32>

[<Literal>]
let Length = 100

type getParent = BlockHeader -> BlockHeader option

let private update op v t =
    (t
     |> Map.tryFind v
     |> Option.defaultValue 0ul
     |> op 1ul
     |> Map.add v) t

//currenty going forward with a more atrophied mechanism, no undo support - recalc on every block
let private handleBlock bk = update (+) bk.version
//let undoBlock = update (-) bk.version

let calc (gp:getParent) tip =
    let rec calc' bk (state : (int * T) option) =
        Option.bind (fun (c, t) ->
            if c = Length || bk.parent = Hash.zero then
                Some t
            else
                Option.bind (fun parent ->
                    calc' parent (Some (c+1, handleBlock bk t))) (gp bk)
        ) state

    calc' tip (Some (0, Map.empty))
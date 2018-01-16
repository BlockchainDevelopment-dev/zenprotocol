module Consensus.Contract

open System.Reflection
open System.Text
open FsBech32
open Hash
open Consensus.TxSkeleton
open Consensus.ZFStar
open Infrastructure
open Zen.Types.Extracted
open Exception

type ContractFn = Hash -> UtxoSet.T -> TxSkeleton -> Result<TxSkeleton,string>

type T = {
    hash: Hash
    fn:   ContractFn
}

let private findMethod (assembly : Assembly) =
    try
        assembly
            .GetModules().[0]
            .GetTypes().[0]
            .GetMethods().[0]
            |> Ok
    with _ as ex ->
        Exception.toError "get method" ex

let private invoke (methodInfo:MethodInfo) cHash input = 
    try
        methodInfo.Invoke (null, [| input; cHash |]) |> Ok
    with _ as ex ->
        Exception.toError "invoke contract" ex

let private castOutput (output:System.Object) =
    try
        output :?> transactionSkeleton |> Ok
    with _ as ex ->
        Exception.toError "cast contract output" ex

let convertResult' set result = 
    match convertResult set result with
    | Some result -> Ok result  
    | None -> Error "contract result is missing outputs"

let private wrap (methodInfo : MethodInfo) : ContractFn =
    fun (Hash.Hash cHash) set txSkeleton ->
        convertInput txSkeleton
        |> invoke methodInfo cHash
        |> Result.bind castOutput
        |> Result.bind (convertResult' set)

let hash contract = contract.hash

let computeHash (code:string) =    
    code
    |> Encoding.UTF8.GetBytes
    |> Hash.compute

let compile (code : string) : Result<T, string> =
    let hash = computeHash code

    hash 
    |> Hash.bytes
    |> Base16.encode
    |> ZFStar.compile code
    |> Result.bind findMethod 
    |> Result.map wrap
    |> Result.map (fun fn ->
        {
            hash = hash
            fn = fn
        })                

let run (contract : T) = 
    contract.fn contract.hash
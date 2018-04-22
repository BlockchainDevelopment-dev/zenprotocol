﻿module Consensus.TransactionValidation

open Consensus
open Types
open UtxoSet
open Crypto
open Zen.Types.Data
open Infrastructure
open Result

type ValidationError =
    | Orphan
    | DoubleSpend
    | ContractNotActive
    | BadContract
    | General of string

let result = new Result.ResultBuilder<ValidationError>()

let private GeneralError msg =
    msg |> General |> Error

let private addSpend s m =
    let (+) a b =
        try
            Some (Operators.Checked.(+) a b) with
        | :? System.OverflowException -> None
    match Map.tryFind s.asset m with
    | Some (Some v) -> Map.add s.asset (v + s.amount) m
    | Some None -> m
    | None -> Map.add s.asset (0UL + s.amount) m

let private foldSpends =
    List.fold (fun map s -> addSpend s map) Map.empty

let private checkSpends m =
    Map.forall (fun _ v -> Option.isSome v) m

let private getSacrificeBlocks (chainParams : Chain.ChainParameters) code sacrifice =
    let codeLength = String.length code |> uint64
    let activationSacrificePerBlock = chainParams.sacrificePerByteBlock * codeLength
    let numberOfBlocks = sacrifice / activationSacrificePerBlock |> uint32

    if numberOfBlocks = 0ul then
        GeneralError "Contract must be activated for at least one block"
    else
        Ok numberOfBlocks

let private activateContract (chainParams : Chain.ChainParameters) contractPath blockNumber acs contractCache (tx : Types.Transaction) =
    let getActivationSacrifice tx = result {
        let activationSacrifices = List.filter(fun output -> output.lock = ActivationSacrifice) tx.outputs

        if List.isEmpty activationSacrifices then
            return! GeneralError "Contract activation must include activation sacrifice"

        let allUsingZen =
            List.forall (fun output -> output.spend.asset = Constants.Zen) activationSacrifices

        if not allUsingZen then
            return! GeneralError "Sacrifice must be paid in Zen"

        return List.sumBy (fun output -> output.spend.amount) activationSacrifices
    }

    result {
        match tx.contract with
        | Some contract ->
            match contract with
            | V0 contract ->
                match ZFStar.totalQueries contract.hints with
                | Error error ->
                    yield! GeneralError error
                | Ok value when value <> contract.queries ->
                    yield! GeneralError "Total queries mismatch"
                | _ -> ()
    
                let cHash = Contract.computeHash contract.code
    
                let! activationSacrifices = getActivationSacrifice tx
    
                let! numberOfBlocks = getSacrificeBlocks chainParams contract.code activationSacrifices
    
                match ActiveContractSet.tryFind cHash acs with
                | Some contract ->
                    let contract = {contract with expiry = contract.expiry + numberOfBlocks}
                    return ActiveContractSet.add cHash contract acs, contractCache
                | None ->
                    let! contract, contractCache = result {
                        match ContractCache.tryFind cHash contractCache with
                        | Some (mainFn, costFn) ->
                            return (
                                {
                                    hash = cHash
                                    mainFn = mainFn
                                    costFn = costFn
                                    expiry = numberOfBlocks
                                    code = contract.code
                                } : Contract.T), contractCache
                        | None ->
                            let compile contract = 
                                Contract.compile contractPath contract
                                |> Result.bind (fun _ -> Contract.load contractPath (blockNumber + numberOfBlocks) contract.code cHash)
                                |> Result.mapError (fun _ -> BadContract)
                                
                            let! contract = Measure.measure (sprintf "compiling contract %A" cHash) (lazy (compile contract))
                            let contractCache = ContractCache.add contract contractCache
                            
                            return contract, contractCache
                    }
                    return ActiveContractSet.add contract.hash contract acs, contractCache
            | HighV _ ->
                return acs, contractCache
        | None ->
            return acs, contractCache
    }

let private extendContracts chainParams acs tx = result {
    let extensionSacrifices = List.filter(function | { lock = ExtensionSacrifice _ } -> true | _ -> false) tx.outputs
    if List.exists (fun output -> output.spend.asset <> Constants.Zen) extensionSacrifices then
        return! GeneralError "Sacrifice must be paid in Zen"

    return!
        extensionSacrifices
        |> List.choose (function | { lock = ExtensionSacrifice cHash; spend = { amount = amount } } -> Some (cHash, amount)
                                 | _ -> None)
        |> List.fold (fun acs (cHash, amount) -> result {
            let! acs = acs
            let! contract = match ActiveContractSet.tryFind cHash acs with
                            | Some contract -> Ok contract
                            | None -> GeneralError "Contract(s) must be active"
            let! blocks = getSacrificeBlocks chainParams contract.code amount
            let contract = {contract with expiry = contract.expiry + blocks}
            return ActiveContractSet.add contract.hash contract acs
        }) (Ok acs)
}

let private checkAmounts (txSkeleton:TxSkeleton.T) =
    let inputs = List.map (function | TxSkeleton.Input.PointedOutput (_, output) -> output.spend | TxSkeleton.Input.Mint spend -> spend) txSkeleton.pInputs
    let inputs, outputs = foldSpends inputs, foldSpends (List.map (fun o -> o.spend) txSkeleton.outputs)

    if not <| checkSpends outputs then
        GeneralError "outputs overflow"
    else if not <| checkSpends inputs then
        GeneralError "inputs overflow"
    else if outputs <> inputs then
        GeneralError "invalid amounts"
    else
        Ok ()

let getContractWallet (txSkeleton:TxSkeleton.T) cw =
    txSkeleton.pInputs.[int cw.beginInputs .. int cw.endInputs]
    |> List.choose (fun input ->
        match input with
        | TxSkeleton.PointedOutput (outpoint,output) when output.lock = Contract cw.cHash ->
            Some (outpoint,output)
        | _ -> None
    )

let private checkWitnesses blockNumber acs (txHash, tx, inputs) =
    let fulltxSkeleton = TxSkeleton.fromTransaction tx inputs

    let checkPKWitness inputTx pInputs publicKey signature =
        let verifyPkHash pkHash tail =
            if PublicKey.hash publicKey = pkHash then
                match verify publicKey signature txHash with
                | Valid -> Ok (inputTx, tail)
                | _ -> GeneralError "invalid PK witness signature"
            else GeneralError "PK witness mismatch"

        match pInputs with
        | [] -> GeneralError "missing PK witness input"
        | TxSkeleton.Input.PointedOutput (_, {lock=Coinbase (coinbaseBlockNumber,pkHash)}) :: tail ->
            if blockNumber - coinbaseBlockNumber < CoinbaseMaturity then
                GeneralError "Coinbase not mature enough"
            else
                verifyPkHash pkHash tail
        | TxSkeleton.Input.PointedOutput (_, {lock=PK pkHash}) :: tail -> verifyPkHash pkHash tail
        | _ -> GeneralError "unexpected PK witness lock type"

    let checkContractWitness inputTx acs cw callingContract message pInputs =
        let checkMessage (txSkeleton, resultMessage) =
            if message = resultMessage then
                Ok txSkeleton
            else
                GeneralError "invalid message"

        let checkIssuedAndDestroyed (txSkeleton : TxSkeleton.T) =
            let isMismatchedSpend ({asset=cHash,_;amount=_} : Spend) =
                cHash <> cw.cHash
            let endInputs = cw.beginInputs + cw.inputsLength - 1u |> int
            let endOutputs = cw.beginOutputs + cw.outputsLength - 1u |> int

            if  endInputs >= List.length txSkeleton.pInputs ||
                endOutputs >= List.length txSkeleton.outputs
            then
                GeneralError "invalid contract witness indices"
            else if
                txSkeleton.pInputs.[int cw.beginInputs .. endInputs]
                |> List.exists (function
                    | TxSkeleton.Input.Mint spend when isMismatchedSpend spend -> true
                    | _ -> false)
            then
                GeneralError "illegal creation of tokens"
            else if
                txSkeleton.outputs.[int cw.beginOutputs .. endOutputs]
                |> List.exists (function
                    | {lock=Destroy; spend=spend} when isMismatchedSpend spend -> true
                    | _ -> false)
            then
                GeneralError "illegal destruction of tokens"
            else
                Ok txSkeleton

        let checkSender =
            match callingContract, cw.signature with
            | Some cHash, None ->
                let cHash = cHash
                Ok <| ContractSender cHash
            | None, Some (publicKey, signature) ->
                match Crypto.verify publicKey signature txHash with
                | Valid ->
                    Ok (PKSender publicKey)
                | Invalid ->
                    GeneralError "invalid contract witness signature"
            | None, None -> Ok Anonymous
            | Some _, Some _ -> GeneralError "invalid contract witness, chained contract cannot have a signature"

        let rec popContractsLocksOf cHash pInputs left =
            if left = 0ul then
                pInputs
            else
                match pInputs with
                | [] -> []
                | TxSkeleton.Input.PointedOutput (_, output) :: tail ->
                    match output.lock with
                    | Contract cHash' when cHash' = cHash ->
                        popContractsLocksOf cHash' tail (left - 1ul)
                    | _ -> pInputs
                | TxSkeleton.Input.Mint mint :: tail when fst mint.asset = cHash ->
                    popContractsLocksOf cHash tail (left - 1ul)
                | _ -> pInputs

        match checkSender with
        | Ok sender ->
            match ActiveContractSet.tryFind cw.cHash acs with
            | Some contract ->
                let contractWallet = getContractWallet fulltxSkeleton cw

                // Validate true cost (not weight!) of running contract against
                // the witness commitment
                let cost = Contract.getCost contract inputTx cw.command sender cw.data contractWallet
                if uint32 cost <> cw.cost then
                    GeneralError <| sprintf "Contract witness committed to cost %d, but cost of execution is %d" (uint32 cost) cw.cost
                else
                    Contract.run contract inputTx cw.command sender cw.data contractWallet
                    |> Result.mapError General
                    |> Result.bind checkMessage
                    |> Result.bind checkIssuedAndDestroyed
                    |> Result.bind (fun outputTx ->
                        if List.length outputTx.pInputs - List.length inputTx.pInputs = int cw.inputsLength &&
                           List.length outputTx.outputs - List.length inputTx.outputs = int cw.outputsLength then

                            Ok (outputTx, popContractsLocksOf cw.cHash pInputs cw.inputsLength)
                        else GeneralError "input/output length mismatch")
            | None -> Error ContractNotActive
        | Error error -> Error error

    let checkHighVWitness inputTx pInputs =
        match pInputs with
        | [] -> GeneralError "missing HighV witness input"
        | _ :: tail -> Ok (inputTx, tail)

    let witnessesFolder state (witness, callingContract, message) =
        state
        |> Result.bind (fun (tx', pInputs) ->
            match witness with
            | PKWitness (serializedPublicKey, signature) ->
                checkPKWitness tx' pInputs serializedPublicKey signature
            | ContractWitness cw ->
                checkContractWitness tx' acs cw callingContract message pInputs
            | HighVWitness _ ->
                checkHighVWitness tx' pInputs
        )

    let applyMaskIfContract pTx =
        match List.tryPick
            (function
            | ContractWitness cw -> Some cw
            | _ -> None) tx.witnesses with
        | Some cw ->
            TxSkeleton.applyMask pTx cw
        | _ ->
            Ok pTx

    result {
        let! masked = applyMaskIfContract fulltxSkeleton |> Result.mapError General

        let! (witnessedSkel, pInputs) =
            List.mapi (fun i witness -> (i, witness)) tx.witnesses
            |> List.map (fun (i, witness) ->
                let nextMessage =
                    match witness with
                    | ContractWitness _ ->
                      if i + 1 = List.length tx.witnesses then
                          None
                      else
                          match tx.witnesses.[i+1] with
                          | ContractWitness cw ->
                              Some { cHash = cw.cHash; command = cw.command; data = cw.data }
                          | _ ->
                              None
                    | _ -> None

                let callingContract =
                    match witness with
                    | ContractWitness _ ->
                        if i = 0 then
                            None
                        else
                            match tx.witnesses.[i-1] with
                            | ContractWitness cw ->
                                Some cw.cHash
                            | _ ->
                                None
                    | _ -> None

                witness, callingContract, nextMessage)

            |> List.fold witnessesFolder (Ok (masked, fulltxSkeleton.pInputs))

        if not <| List.isEmpty pInputs then
            return! GeneralError "missing witness(es)"
        elif not <| TxSkeleton.isSkeletonOf witnessedSkel tx inputs then
            return! GeneralError "contract validation failed"
        else
            return witnessedSkel
    }

let private checkOutputsOverflow tx =
    if tx.outputs
        |> List.map (fun o -> o.spend)
        |> foldSpends
        |> checkSpends then Ok tx
    else GeneralError "outputs overflow"

let private checkDuplicateInputs tx =
    let (==) a b = List.length a = List.length b
    if List.distinct tx.inputs == tx.inputs then Ok tx
    else GeneralError "inputs duplicated"

let private checkStructure =
    let isInvalidSpend = fun { asset = (cHash, token); amount = amount } ->
        (cHash = Hash.zero && token <> Hash.zero) ||
        amount = 0UL //Relieve for non spendables?
    
    let isNull = function | null -> true | _ -> false
    let isEmptyArr arr = isNull arr || Array.length arr = 0
    let isEmptyString str = isNull str || String.length str = 0

    let checkContractStructure = fun tx ->
        match tx.contract with
        | Some contract ->
            match contract with
            | V0 contract when 
                isEmptyString contract.code ||
                isEmptyString contract.hints ||
                contract.rlimit = 0u ||
                contract.queries = 0u -> false
            | HighV (version, bytes) when 
                version = Version0 || //reserved for V0
                isEmptyArr bytes -> false
            | _ -> true
        | None -> true
        |> function
        | false -> 
            GeneralError "structurally invalid contract data"
        | true -> Ok tx
            
    let checkInputsStructrue = fun tx ->
        if List.isEmpty tx.inputs || List.exists (function
            | Mint spend -> isInvalidSpend spend
            | _ -> false
        ) tx.inputs then
            GeneralError "structurally invalid input(s)"
        else
            Ok tx
            
    let checkOutputsStructrue = fun tx ->
        if List.isEmpty tx.outputs || List.exists (fun { lock = lock; spend = spend } ->
            match lock with
            | HighVLock (identifier, bytes) -> 
                identifier <= 7u // last reserved identifier for lock types
                || isEmptyArr bytes
            | _ -> false
            || isInvalidSpend spend
        ) tx.outputs then
            GeneralError "structurally invalid output(s)"
        else
            Ok tx

    let checkWitnessesStructure = fun tx ->
        if List.isEmpty tx.witnesses || List.exists (function
            | ContractWitness { command = command
                                cost = cost } ->
                isNull command || cost = 0ul
            | HighVWitness (identifier, bytes) ->
                identifier <= 2u // last reserved identifier for witness types
                || isEmptyArr bytes
            | _ -> false
        ) tx.witnesses then
            GeneralError "structurally invalid witness(es)"
        else
            Ok tx

    checkContractStructure
    >=> checkInputsStructrue
    >=> checkOutputsStructrue
    >=> checkWitnessesStructure

let private checkNoCoinbaseLock tx =
    let anyCoinbase =
        List.exists (fun output ->
            match output.lock with
            | Coinbase _ -> true
            | _ -> false) tx.outputs

    if anyCoinbase then
        GeneralError "coinbase lock is not allowed within an ordinary transaction"
    else
        Ok tx

let internal tryGetUtxos getUTXO utxoSet tx =
    getUtxosResult getUTXO tx.inputs utxoSet
    |> Result.mapError (fun errors ->
        if List.exists
            (fun err -> match err with | Spent _ -> true | _ -> false)
            errors
        then DoubleSpend else Orphan
    )

let validateBasic =
    checkStructure
    >=> checkNoCoinbaseLock
    >=> checkOutputsOverflow
    >=> checkDuplicateInputs

let validateCoinbase blockNumber =
    let checkOnlyCoinbaseLocks blockNumber tx =
        let allCoinbase =
            List.forall (fun output ->
                match output.lock with
                | Coinbase (blockNumber',_) when blockNumber' = blockNumber -> true
                | _ -> false) tx.outputs

        if allCoinbase then
            Ok tx
        else
            GeneralError "within coinbase transaction all outputs must use coinbase lock"

    let checkNoInputWithinCoinbaseTx tx =
        match tx.inputs with
        | [] -> Ok tx
        | _ -> GeneralError "coinbase transaction must not have any inputs"

    let checkNoContractInCoinbase tx =
        if Option.isSome tx.contract then
            GeneralError "coinbase transaction cannot activate a contract"
        else
            Ok tx

    let checkNoWitnesses tx =
        match tx.witnesses with
        | [] -> Ok tx
        | _ -> GeneralError "coinbase transaction must not have any witnesses"

    let checkOutputsNotEmpty tx =
        if List.isEmpty tx.outputs then
            GeneralError "outputs empty"
        else if List.exists (fun output -> output.spend.amount = 0UL) tx.outputs then
            GeneralError "outputs invalid"
        else
            Ok tx

    checkOutputsNotEmpty
    >=> checkNoInputWithinCoinbaseTx
    >=> checkNoWitnesses
    >=> checkOnlyCoinbaseLocks blockNumber
    >=> checkNoContractInCoinbase
    >=> checkOutputsOverflow

let validateInContext chainParams getUTXO contractPath blockNumber acs contractCache set txHash tx = result {
    let! outputs = tryGetUtxos getUTXO set tx
    let! txSkel = checkWitnesses blockNumber acs (txHash, tx, outputs)
    do! checkAmounts txSkel
    let! acs, contractCache = activateContract chainParams contractPath blockNumber acs contractCache tx
    let! acs = extendContracts chainParams acs tx
    return tx, acs, contractCache
}

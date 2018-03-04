module Blockchain.DatabaseContext

open DataAccess
open Consensus
open Types
open UtxoSet
open FStar
open Infrastructure
open MBrace.FsPickler
open Serialization

// TODO:Move to own file
// TODO:Implement serialize and deserialize for persistence
type BlockState =
    {
        ema:EMA.T
        activeContractSet:(Hash.Hash*uint32*uint32) list
    }

type T =
    {
        databaseContext:DataAccess.DatabaseContext
        tip:SingleValue<Hash.Hash>
        utxoSet:Collection<Outpoint, OutputStatus>
        contractUtxo:MultiCollection<Hash.Hash,PointedOutput>
        blocks:Collection<Hash.Hash,ExtendedBlockHeader.T>
        blockChildrenIndex: Index<Hash.Hash,ExtendedBlockHeader.T,Hash.Hash>
        blockState:Collection<Hash.Hash,BlockState>
        blockTransactions: Collection<Hash.Hash, Hash.Hash seq>
        transactions:Collection<Hash.Hash, Transaction>
        transactionBlocks:MultiCollection<Hash.Hash, Hash.Hash>
        contractPath:string
    }
    interface System.IDisposable with
        member x.Dispose () =
            Disposables.dispose x.utxoSet
            Disposables.dispose x.contractUtxo
            Disposables.dispose x.blocks
            Disposables.dispose x.blockChildrenIndex
            Disposables.dispose x.blockState
            Disposables.dispose x.blockTransactions
            Disposables.dispose x.transactions
            Disposables.dispose x.transactionBlocks
            Disposables.dispose x.databaseContext


type Session =
    {
        session: DataAccess.Session
        context: T
    }
    interface System.IDisposable with
        member x.Dispose () = Disposables.dispose x.session

let binarySerializer = FsPickler.CreateBinarySerializer()

let private serializeHashes hs =
    hs
    |> Seq.map Hash.bytes
    |> Array.concat

let private deserializeHashes bytes =
    bytes
    |> Array.chunkBySize Hash.Length
    |> Array.toSeq
    |> Seq.map Hash.Hash

let createSession context : Session =
    let session = DataAccess.DatabaseContext.createSession context.databaseContext
    {
        session=session
        context=context
    }

let create dataPath =
    let databaseContext = DataAccess.DatabaseContext.create (Platform.combine dataPath "blockchain")
    use session = DatabaseContext.createSession databaseContext

    let tip = SingleValue.create databaseContext "tip" Hash.bytes Hash.Hash

    let blocks = Collection.create session "blocks" Hash.bytes
                    binarySerializer.Pickle<ExtendedBlockHeader.T> binarySerializer.UnPickle<ExtendedBlockHeader.T>

    let blockChildrenIndex =
        Index.create session blocks "blockChildren" Hash.Length Hash.bytes (fun _ key (value:ExtendedBlockHeader.T) ->
            value.header.parent,key)

    let blockState = Collection.create session "blockState" Hash.bytes
                        binarySerializer.Pickle<BlockState> binarySerializer.UnPickle<BlockState>

    let blockTransactions = Collection.create session "blockTransactions" Hash.bytes serializeHashes deserializeHashes

    let transactions = Collection.create session "transactions" Hash.bytes
                        (serializeTransaction Full) (deserializeTransaction >> Option.get)

    let transactionBlocks = MultiCollection.create session "transactionBlocks"
                                Hash.bytes Hash.bytes Hash.Hash

    let blocks =
        blocks
        |> Collection.addIndex blockChildrenIndex

    let utxoSet =
        Collection.create session "utxoSet"
            binarySerializer.Pickle<Outpoint>
            binarySerializer.Pickle<OutputStatus>
            binarySerializer.UnPickle<OutputStatus>

    let contractUtxo =
        MultiCollection.create session "contractUtxo" Hash.bytes
            binarySerializer.Pickle<PointedOutput>
            binarySerializer.UnPickle<PointedOutput>

    Session.commit session

    {
        databaseContext = databaseContext
        tip=tip
        utxoSet=utxoSet
        contractUtxo=contractUtxo
        blocks=blocks
        blockChildrenIndex=blockChildrenIndex
        blockState=blockState
        blockTransactions=blockTransactions
        transactions=transactions
        transactionBlocks = transactionBlocks
        contractPath=(Platform.combine dataPath "contracts")
    }

let createEmpty pathToFolder =
    if System.IO.Directory.Exists pathToFolder then
       System.IO.Directory.Delete (pathToFolder,true)

    create pathToFolder


let createChildSession (session:Session) =
    let childSession = DataAccess.DatabaseContext.createChildSession session.session
    {
        session=childSession
        context=session.context
    }
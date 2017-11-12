module Consensus.Tests.CryptoTests

open Xunit
open FsUnit.Xunit
open Consensus.Crypto
open Consensus.Hash

[<Fact>]
let ``sign and verify``() = 
    let secretKey, publicKey = KeyPair.create()
    
    let msg = Array.create MessageLength 0uy
    let random = new System.Random()
    
    random.NextBytes(msg)
    
    let signature = sign secretKey (Hash msg)
    
    let result = verify publicKey signature msg
    
    result |> should equal VerifyResult.Valid
    
[<Fact>]
let ``serialize and deserialize publickey``() = 
    let _, publicKey = KeyPair.create ()
    
    let bytes = PublicKey.serialize publicKey
    
    let publicKey' = PublicKey.deserialize bytes
    
    publicKey' |> should equal (Some publicKey)

[<Fact>]
let ``serialize and deserialize signature``() =
    let secretKey, publicKey = KeyPair.create()
            
    let msg = Array.create MessageLength 0uy
    let random = new System.Random()
    
    random.NextBytes(msg)
    
    let signature = sign secretKey (Hash msg)
    
    let serialized = Signature.serialize signature
    
    let signature' = Signature.deserialize serialized

    signature' |> should equal (Some signature)
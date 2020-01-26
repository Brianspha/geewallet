﻿namespace GWallet.Backend

open System
open System.IO
open System.Linq

module ServerManager =

    let UpdateServersFile () =
        Console.WriteLine "INPUT:"
        let baseLineServers = Config.ExtractEmbeddedResourceFileContents ServerRegistry.ServersEmbeddedResourceFileName
                              |> ServerRegistry.Deserialize

        let fromElectrumServerToGenericServerDetails (es: UtxoCoin.ElectrumServer) =
            match es.UnencryptedPort with
            | None -> failwith "filtering for non-ssl electrum servers didn't work?"
            | Some unencryptedPort ->
                {
                    ServerInfo =
                        {
                            NetworkPath = es.Fqdn
                            ConnectionType = { Encrypted = false; Protocol = Tcp unencryptedPort }
                        }
                    CommunicationHistory = None
                }

        let btc = Currency.BTC
        let electrumBtcServers = UtxoCoin.ElectrumServerSeedList.ExtractServerListFromElectrumRepository btc
        let eyeBtcServers = UtxoCoin.ElectrumServerSeedList.ExtractServerListFromWebPage btc

        let baseLineBtcServers =
            match baseLineServers.TryGetValue btc with
            | true,baseLineBtcServers ->
                baseLineBtcServers
            | false,_ ->
                failwithf "There should be some %A servers as baseline" btc

        let allBtcServers = Seq.append electrumBtcServers eyeBtcServers
                            |> Seq.map fromElectrumServerToGenericServerDetails
                            |> Seq.append baseLineBtcServers

        let ltc = Currency.LTC
        let electrumLtcServers = UtxoCoin.ElectrumServerSeedList.ExtractServerListFromElectrumRepository ltc
        let eyeLtcServers = UtxoCoin.ElectrumServerSeedList.ExtractServerListFromWebPage ltc

        let baseLineLtcServers =
            match baseLineServers.TryGetValue ltc with
            | true,baseLineLtcServers ->
                baseLineLtcServers
            | false,_ ->
                failwithf "There should be some %A servers as baseline" ltc

        let allLtcServers = Seq.append electrumLtcServers eyeLtcServers
                            |> Seq.map fromElectrumServerToGenericServerDetails
                            |> Seq.append baseLineLtcServers

        for KeyValue(currency,servers) in baseLineServers do
            Console.WriteLine (sprintf "%i %A servers from baseline JSON file" (servers.Count()) currency)

            match currency with
            | Currency.BTC ->
                Console.WriteLine (sprintf "%i BTC servers from electrum repository" (electrumBtcServers.Count()))
                Console.WriteLine (sprintf "%i BTC servers from bitcoin-eye" (eyeBtcServers.Count()))
            | Currency.LTC ->
                Console.WriteLine (sprintf "%i LTC servers from electrum repository" (electrumLtcServers.Count()))
                Console.WriteLine (sprintf "%i LTC servers from bitcoin-eye" (eyeLtcServers.Count()))
            | _ ->
                ()

        let allCurrenciesServers =
            baseLineServers.Add(Currency.BTC, allBtcServers)
                           .Add(Currency.LTC, allLtcServers)

        let allServersJson = ServerRegistry.Serialize allCurrenciesServers
        File.WriteAllText(ServerRegistry.ServersEmbeddedResourceFileName, allServersJson)

        Console.WriteLine "OUTPUT:"
        let filteredOutServers = ServerRegistry.Deserialize allServersJson
        for KeyValue(currency,servers) in filteredOutServers do
            Console.WriteLine (sprintf "%i %A servers total" (servers.Count()) currency)

    let private tester =
        FaultTolerantParallelClient<ServerDetails,CommunicationUnsuccessfulException>
            Caching.Instance.SaveServerLastStat

    let private testingSettings =
        {
            NumberOfParallelJobsAllowed = 1u

            // if not zero we might screw up our percentage logging when performing the requests?
            NumberOfRetries = 0u
            NumberOfRetriesForInconsistency = 0u

            ResultSelectionMode = Exhaustive

            ExceptionHandler = None
        }

    let private GetDummyBalanceAction (currency: Currency) servers =

        let retrievalFuncs =
            if (currency.IsUtxo()) then
                let scriptHash =
                    match currency with
                    | Currency.BTC ->
                        // probably a satoshi address because it was used in blockheight 2 and is unspent yet
                        let SATOSHI_ADDRESS = "1HLoD9E4SDFFPDiYfNYnkBLQ85Y51J3Zb1"
                        // funny that it almost begins with "1HoDL"
                        UtxoCoin.Account.GetElectrumScriptHashFromPublicAddress currency SATOSHI_ADDRESS
                    | Currency.LTC ->
                        // https://medium.com/@SatoshiLite/satoshilite-1e2dad89a017
                        let LTC_GENESIS_BLOCK_ADDRESS = "Ler4HNAEfwYhBmGXcFP2Po1NpRUEiK8km2"
                        UtxoCoin.Account.GetElectrumScriptHashFromPublicAddress currency LTC_GENESIS_BLOCK_ADDRESS
                    | _ ->
                        failwithf "Currency %A not UTXO?" currency
                let utxoFunc electrumServer =
                    async {
                        let! bal = UtxoCoin.ElectrumClient.GetBalance scriptHash electrumServer
                        return bal.Confirmed |> decimal
                    }
                UtxoCoin.Server.GetServerFuncs utxoFunc servers |> Some

            elif currency.IsEther() then
                let ETH_GENESISBLOCK_ADDRESS = "0x0000000000000000000000000000000000000000"

                let web3Func (web3: Ether.SomeWeb3): Async<decimal> =
                    async {
                        let! balance = Async.AwaitTask (web3.Eth.GetBalance.SendRequestAsync ETH_GENESISBLOCK_ADDRESS)
                        return balance.Value |> decimal
                    }

                Ether.Server.GetServerFuncs web3Func servers |> Some

            else
                None

        match retrievalFuncs with
        | Some queryFuncs ->
            async {
                try
                    let! _ = tester.Query testingSettings
                                          (queryFuncs |> List.ofSeq)
                    return ()
                with
                | :? NoneAvailableException ->
                    return ()
            } |> Some
        | _ ->
            None

    let private UpdateBaseline() =
        match Caching.Instance.ExportServers() with
        | None -> failwith "After updating servers, cache should not be empty"
        | Some serversInJson ->
            File.WriteAllText(ServerRegistry.ServersEmbeddedResourceFileName, serversInJson)

    let UpdateServersStats () =
        let jobs currency = seq {
                    let serversForSpecificCurrency = Caching.Instance.GetServers currency
                    match GetDummyBalanceAction currency serversForSpecificCurrency with
                    | None -> ()
                    | Some job -> yield job
        }
        Console.WriteLine("BTC first")
        Async.Parallel (jobs Currency.BTC)
            |> Async.RunSynchronously
            |> ignore
        Console.WriteLine("LTC now")
        Async.Parallel (jobs Currency.LTC)
            |> Async.RunSynchronously
            |> ignore
        Console.WriteLine("=============FINISHED")

        UpdateBaseline()


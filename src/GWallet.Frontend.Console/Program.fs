﻿open System
open System.IO
open System.Linq
open System.Text.RegularExpressions

open GWallet.Backend
open GWallet.Backend.FSharpUtil
open GWallet.Backend.UtxoCoin.Lightning
open GWallet.Frontend.Console

let random = Org.BouncyCastle.Security.SecureRandom () :> Random

let rec TrySendAmount (account: NormalAccount) transactionMetadata destination amount =
    let password = UserInteraction.AskPassword false
    try
        let txIdUri =
            Account.SendPayment account transactionMetadata destination amount password
                |> Async.RunSynchronously
        Console.WriteLine(sprintf "Transaction successful:%s%s" Environment.NewLine (txIdUri.ToString()))
        UserInteraction.PressAnyKeyToContinue ()
    with
    | :? DestinationEqualToOrigin ->
        Presentation.Error "Transaction's origin cannot be the same as the destination."
        UserInteraction.PressAnyKeyToContinue()
    | :? InsufficientFunds ->
        Presentation.Error "Insufficient funds."
        UserInteraction.PressAnyKeyToContinue()
    | :? InvalidPassword ->
        Presentation.Error "Invalid password, try again."
        TrySendAmount account transactionMetadata destination amount

let rec TrySign account unsignedTrans =
    let password = UserInteraction.AskPassword false
    try
        Account.SignUnsignedTransaction account unsignedTrans password
    with
    // TODO: would this throw insufficient funds? test
    //| :? InsufficientFunds ->
    //    Presentation.Error "Insufficient funds."
    | :? InvalidPassword ->
        Presentation.Error "Invalid password, try again."
        TrySign account unsignedTrans

let BroadcastPayment() =
    let fileToReadFrom = UserInteraction.AskFileNameToLoad
                             "Introduce a file name to load the signed transaction: "
    let signedTransaction = Account.LoadSignedTransactionFromFile fileToReadFrom.FullName
    //TODO: check if nonce matches, if not, reject trans

    // FIXME: we should be able to infer the trans info from the raw transaction! this way would be more secure too
    Presentation.ShowTransactionData(signedTransaction.TransactionInfo)
    if UserInteraction.AskYesNo "Do you accept?" then
        try
            let txIdUri =
                Account.BroadcastTransaction signedTransaction
                    |> Async.RunSynchronously
            Console.WriteLine(sprintf "Transaction successful:%s%s" Environment.NewLine (txIdUri.ToString()))
            UserInteraction.PressAnyKeyToContinue ()
        with
        | :? DestinationEqualToOrigin ->
            Presentation.Error "Transaction's origin cannot be the same as the destination."
            UserInteraction.PressAnyKeyToContinue()
        | :? InsufficientFunds ->
            Presentation.Error "Insufficient funds."
            UserInteraction.PressAnyKeyToContinue()

let SignOffPayment() =
    let fileToReadFrom = UserInteraction.AskFileNameToLoad
                             "Introduce a file name to load the unsigned transaction: "
    let unsignedTransaction = Account.LoadUnsignedTransactionFromFile fileToReadFrom.FullName

    let accountsWithSameAddress =
        Account.GetAllActiveAccounts().Where(fun acc -> acc.PublicAddress = unsignedTransaction.Proposal.OriginAddress)
    if not (accountsWithSameAddress.Any()) then
        Presentation.Error "The transaction doesn't correspond to any of the accounts in the wallet."
        UserInteraction.PressAnyKeyToContinue ()
    else
        let accounts =
            accountsWithSameAddress.Where(
                fun acc -> acc.Currency = unsignedTransaction.Proposal.Amount.Currency &&
                           acc :? NormalAccount)
        if not (accounts.Any()) then
            Presentation.Error(
                sprintf
                    "The transaction corresponds to an address of the accounts in this wallet, but it's a readonly account or it maps a different currency than %A."
                    unsignedTransaction.Proposal.Amount.Currency
            )
            UserInteraction.PressAnyKeyToContinue()
        else
            let account = accounts.First()
            if (accounts.Count() > 1) then
                failwith "More than one normal account matching address and currency? Please report this issue."

            match account with
            | :? ReadOnlyAccount ->
                failwith "Previous account filtering should have discarded readonly accounts already. Please report this issue"
            | :? NormalAccount as normalAccount ->
                Console.WriteLine ("Importing external data...")
                Caching.Instance.SaveSnapshot unsignedTransaction.Cache

                Console.WriteLine ("Account to use when signing off this transaction:")
                Console.WriteLine ()
                let linesJob = UserInteraction.DisplayAccountStatuses <| WhichAccount.MatchingWith account
                for line in Async.RunSynchronously linesJob do
                    Console.WriteLine line
                Console.WriteLine()

                Presentation.ShowTransactionData unsignedTransaction

                if UserInteraction.AskYesNo "Do you accept?" then
                    let trans = TrySign normalAccount unsignedTransaction
                    Console.WriteLine("Transaction signed.")
                    Console.Write("Introduce a file name or path to save it: ")
                    let filePathToSaveTo = Console.ReadLine()
                    Account.SaveSignedTransaction trans filePathToSaveTo
                    Console.WriteLine("Transaction signed and saved successfully. Now copy it to the online device.")
                    UserInteraction.PressAnyKeyToContinue ()
            | _ ->
                failwith "Account type not supported. Please report this issue."

let SendPaymentOfSpecificAmount (account: IAccount)
                                (amount: TransferAmount)
                                (transactionMetadata: IBlockchainFeeInfo)
                                (destination: string) =
    match account with
    | :? ReadOnlyAccount ->
        Console.WriteLine("Cannot send payments from readonly accounts.")
        Console.Write("Introduce a file name to save the unsigned transaction: ")
        let filePath = Console.ReadLine()
        let proposal = {
            OriginAddress = account.PublicAddress;
            Amount = amount;
            DestinationAddress = destination;
        }
        Account.SaveUnsignedTransaction proposal transactionMetadata filePath
        Console.WriteLine("Transaction saved. Now copy it to the device with the private key.")
        UserInteraction.PressAnyKeyToContinue()
    | :? NormalAccount as normalAccount ->
        TrySendAmount normalAccount transactionMetadata destination amount
    | _ ->
        failwith ("Account type not recognized: " + account.GetType().FullName)

let SendPayment() =
    let account = UserInteraction.AskAccount()
    let destination = UserInteraction.AskPublicAddress account.Currency "Destination address: "
    let maybeAmount = UserInteraction.AskAmount account
    match maybeAmount with
    | None -> ()
    | Some(amount) ->
        let maybeFee = UserInteraction.AskFee account amount destination
        match maybeFee with
        | None -> ()
        | Some(fee) ->
            SendPaymentOfSpecificAmount account amount fee destination

let rec TryArchiveAccount account =
    let password = UserInteraction.AskPassword(false)
    try
        Account.Archive account password
        Console.WriteLine "Account archived."
        UserInteraction.PressAnyKeyToContinue ()
    with
    | :? InvalidPassword ->
        Presentation.Error "Invalid password, try again."
        TryArchiveAccount account

let rec AddReadOnlyAccounts() =
    Console.Write "JSON fragment from wallet to pair with: "
    let watchWalletInfoJson = Console.ReadLine().Trim()
    let watchWalletInfo = Marshalling.Deserialize watchWalletInfoJson
    Account.CreateReadOnlyAccounts watchWalletInfo

let ArchiveAccount() =
    let account = UserInteraction.AskAccount()
    match account with
    | :? ReadOnlyAccount as readOnlyAccount ->
        Console.WriteLine("Read-only accounts cannot be archived, but just removed entirely.")
        if not (UserInteraction.AskYesNo "Do you accept?") then
            ()
        else
            Account.Remove readOnlyAccount
            Console.WriteLine "Read-only account removed."
            UserInteraction.PressAnyKeyToContinue()
    | :? NormalAccount as normalAccount ->
        let balance,_ =
            Account.GetShowableBalanceAndImminentIncomingPayment account ServerSelectionMode.Fast None
                |> Async.RunSynchronously
        match balance with
        | NotFresh(NotAvailable) ->
            Presentation.Error "Removing accounts when offline is not supported."
            ()
        | Fresh(amount) | NotFresh(Cached(amount,_)) ->
            if (amount > 0m) then
                Presentation.Error "Please empty the account before removing it."
                UserInteraction.PressAnyKeyToContinue ()
            else
                Console.WriteLine ()
                Console.WriteLine "Please note: "
                Console.WriteLine "Just in case this account receives funds in the future by mistake, "
                Console.WriteLine "the operation of archiving an account doesn't entirely remove it."
                Console.WriteLine ()
                Console.WriteLine "You will be asked the password of it now so that its private key can remain unencrypted in the configuration folder, in order for you to be able to safely forget this password."
                Console.WriteLine "Then this account will be watched constantly and if new payments are detected, "
                Console.WriteLine "GWallet will prompt you to move them to a current account without the need of typing the old password."
                Console.WriteLine ()
                if not (UserInteraction.AskYesNo "Do you accept?") then
                    ()
                else
                    TryArchiveAccount normalAccount
    | _ ->
        failwithf "Account type not valid for archiving: %s. Please report this issue."
                  (account.GetType().FullName)

let PairToWatchWallet() =
    match Account.GetNormalAccountsPairingInfoForWatchWallet() with
    | None ->
        Presentation.Error
            "There needs to be both Ether-based accounts and Utxo-based accounts to be able to use this feature."
    | Some walletInfo ->
        Console.WriteLine ""
        Console.WriteLine "Copy/paste this JSON fragment in your watching wallet:"
        Console.WriteLine ""
        let json = Marshalling.Serialize walletInfo
        Console.WriteLine json
        Console.WriteLine ""

    UserInteraction.PressAnyKeyToContinue()

type private GenericWalletOption =
    | Cancel
    | TestPaymentPassword
    | TestSeedPassphrase
    | WipeWallet

let rec TestPaymentPassword () =
    let password = UserInteraction.AskPassword false
    let passwordChecksOnAllAccounts = Account.CheckValidPassword password |> Async.RunSynchronously
    if not (passwordChecksOnAllAccounts.All(fun x -> x = true)) then
        Console.WriteLine "Try again."
        TestPaymentPassword ()

let rec TestSeedPassphrase(): unit =
    let passphrase,dob,email = UserInteraction.AskBrainSeed false
    let check = Account.CheckValidSeed passphrase dob email |> Async.RunSynchronously
    if not check then
        Console.WriteLine "Try again."
        TestSeedPassphrase()

let WipeWallet() =
    Console.WriteLine "If you want to remove accounts, the recommended way is to archive them, not wipe the whole wallet."
    Console.Write "Are you ABSOLUTELY SURE about this? If yes, write 'YES' in uppercase: "
    let sure = Console.ReadLine ()
    if sure = "YES" then
        Account.WipeAll()
    else
        ()

let WalletOptions(): unit =
    let rec AskWalletOption(): GenericWalletOption =
        Console.WriteLine "0. Cancel, go back"
        Console.WriteLine "1. Check you still remember your payment password"
        Console.WriteLine "2. Check you still remember your seed passphrase"
        Console.WriteLine "3. Wipe your current wallet, in order to start from scratch"
        Console.Write "Choose an option from the ones above: "
        let optIntroduced = Console.ReadLine ()
        match UInt32.TryParse optIntroduced with
        | false, _ -> AskWalletOption()
        | true, optionParsed ->
            match optionParsed with
            | 0u -> GenericWalletOption.Cancel
            | 1u -> GenericWalletOption.TestPaymentPassword
            | 2u -> GenericWalletOption.TestSeedPassphrase
            | 3u -> GenericWalletOption.WipeWallet
            | _ -> AskWalletOption()

    let walletOption = AskWalletOption()
    match walletOption with
    | GenericWalletOption.TestPaymentPassword ->
        TestPaymentPassword()
        Console.WriteLine "Success!"
    | GenericWalletOption.TestSeedPassphrase ->
        TestSeedPassphrase()
        Console.WriteLine "Success!"
    | GenericWalletOption.WipeWallet ->
        WipeWallet()
    | _ -> ()

type ChannelCreationDetails =
    {
        Seed: NBitcoin.uint256
        AcceptChannel: DotNetLightning.Serialize.Msgs.AcceptChannel
        Channel: DotNetLightning.Channel.Channel
        Connection: Lightning.Connection
        Password: Ref<string>
    }

let AskChannelFee (account: UtxoCoin.NormalUtxoAccount)
                  (channelCapacity: TransferAmount)
                  (balance: decimal)
                  (channelCounterpartyIP: System.Net.IPEndPoint)
                  (channelCounterpartyPubKey: NBitcoin.PubKey)
                      : Async<Option<ChannelCreationDetails>> =
    // this dummy address is only used for fee estimation
    let nullScriptId = NBitcoin.WitScriptId (Array.zeroCreate 32)
    let dummyAddr = NBitcoin.BitcoinWitScriptAddress (nullScriptId, Config.BitcoinNet)

    DebugLogger "Calling EstimateFee..."
    let metadata =
        try
            UtxoCoin.Account.EstimateFeeForDestination
                 account channelCapacity dummyAddr
                 |> Async.RunSynchronously
        with
        | InsufficientBalanceForFee _ ->
            failwith "Estimated fee is too high for the remaining balance, \
                      use a different account or a different amount."
    Presentation.ShowFee Currency.BTC metadata

    let channelKeysSeed, keyRepo, temporaryChannelId = Lightning.GetSeedAndRepo random
    let channelEnvironment: Lightning.ChannelEnvironment =
        {
            Account = account
            NodeIdForResponder = DotNetLightning.Utils.Primitives.NodeId channelCounterpartyPubKey
            KeyRepo = keyRepo
        }
    async {
        let! connectionBeforeAcceptChannel =
            Lightning.ConnectAndHandshake channelEnvironment channelCounterpartyIP
        let passwordRef = ref "DotNetLightning shouldn't ask for password until later when user has
                               confirmed the funding transaction fee. So this is a placeholder."
        let! acceptChannel, chan, peer =
            Lightning.GetAcceptChannel
                channelEnvironment
                connectionBeforeAcceptChannel
                channelCapacity
                metadata
                (fun _ -> !passwordRef)
                balance
                temporaryChannelId
        let connectionWithNewPeer = { connectionBeforeAcceptChannel with Peer = peer }
        printfn
            "Opening a channel with this party will require %d confirmations (~%d minutes)"
            acceptChannel.MinimumDepth.Value
            (acceptChannel.MinimumDepth.Value * 10u)
        let accept = UserInteraction.AskYesNo "Do you accept?"
        return
            if accept then
                Some
                    {
                        ChannelCreationDetails.Seed = channelKeysSeed
                        AcceptChannel = acceptChannel
                        Channel = chan
                        Connection = connectionWithNewPeer
                        Password = passwordRef
                    }
            else
                connectionWithNewPeer.Client.Dispose()
                None
    }

let OptionFromMaybeCachedBalance (balance: MaybeCached<decimal>): Option<decimal> =
    match balance with
    | NotFresh NotAvailable ->
        Presentation.Error "Can't open channel while offline."
        None
    | Fresh balance | NotFresh (Cached(balance, _)) ->
        Some balance

// Alternative concise definition: http://www.fssnip.net/5Y/title/Repeat-until
let rec RetryOptionFunctionUntilSome (functionToRetry: unit -> Option<'T>): 'T =
    let returnedValue: Option<'T> = functionToRetry ()
    match returnedValue with
    | Some x -> x
    | None ->
        RetryOptionFunctionUntilSome functionToRetry

let rec PerformOperation (numAccounts: int) =
    match UserInteraction.AskOperation numAccounts with
    | Operations.Exit -> exit 0
    | Operations.CreateAccounts ->
        let bootstrapTask = Caching.Instance.BootstrapServerStatsFromTrustedSource() |> Async.StartAsTask
        let passphrase,dob,email = UserInteraction.AskBrainSeed true
        if null <> bootstrapTask.Exception then
            raise bootstrapTask.Exception
        let masterPrivateKeyTask =
            Account.GenerateMasterPrivateKey passphrase dob email
                |> Async.StartAsTask
        let password = UserInteraction.AskPassword true
        Async.RunSynchronously (Account.CreateAllAccounts masterPrivateKeyTask password)
        Console.WriteLine("Accounts created")
        UserInteraction.PressAnyKeyToContinue()
    | Operations.Refresh -> ()
    | Operations.SendPayment ->
        SendPayment()
    | Operations.AddReadonlyAccounts ->
        AddReadOnlyAccounts()
            |> Async.RunSynchronously
    | Operations.SignOffPayment ->
        SignOffPayment()
    | Operations.BroadcastPayment ->
        BroadcastPayment()
    | Operations.ArchiveAccount ->
        ArchiveAccount()
    | Operations.PairToWatchWallet ->
        PairToWatchWallet()
    | Operations.Options ->
        WalletOptions()
    | Operations.OpenChannel ->
        let btcAccount = Account
                             .GetAllActiveAccounts()
                             .OfType<UtxoCoin.NormalUtxoAccount>()
                             .Single(fun account -> (account :> IAccount).Currency = Currency.BTC)
        let balance, _ = Account.GetShowableBalanceAndImminentIncomingPayment
                             btcAccount ServerSelectionMode.Fast None
                             |> Async.RunSynchronously
        let maybeChannelCreationDetails =
            FSharpUtil.option {
                let! balance = OptionFromMaybeCachedBalance balance
                let! channelCapacity = UserInteraction.AskAmount btcAccount
                let! ipEndpoint, pubKey = UserInteraction.AskChannelCounterpartyConnectionDetails()
                DebugLogger "Getting channel fee..."
                let! channelCreationDetails =
                    AskChannelFee
                        btcAccount
                        channelCapacity
                        balance
                        ipEndpoint
                        pubKey
                        |> Async.RunSynchronously
                return ipEndpoint, channelCreationDetails
            }
        match maybeChannelCreationDetails with
        | Some (ipEndpoint, details) ->
            DebugLogger "Opening channel..."
            let txId =
                let attempt (): Option<string> =
                    let password = UserInteraction.AskPassword false
                    // Password is a reference, it is also inside details.Channel,
                    // so while it looks unused, it is indeed used.
                    details.Password := password
                    try
                        let txId =
                            Lightning.ContinueFromAcceptChannelAndSave
                                btcAccount
                                details.Seed
                                ipEndpoint
                                details.AcceptChannel
                                details.Channel
                                (details.Connection.Client.GetStream())
                                details.Connection.Peer
                                |> Async.RunSynchronously
                        details.Connection.Client.Dispose()
                        Some txId
                    with
                    | :? InvalidPassword ->
                        printfn "Invalid password, try again."
                        None
                RetryOptionFunctionUntilSome attempt
            let uri = BlockExplorer.GetTransaction Currency.BTC txId
            printfn "A funding transaction was broadcast: %A" uri
            UserInteraction.PressAnyKeyToContinue()
        | None ->
            () // Error message printed already
    | Operations.AcceptChannel ->
        let btcAccount = Account
                             .GetAllActiveAccounts()
                             .OfType<UtxoCoin.NormalUtxoAccount>()
                             .Single(fun account -> (account :> IAccount).Currency = Currency.BTC)
        Lightning.AcceptTheirChannel random btcAccount |> Async.RunSynchronously
    | _ -> failwith "Unreachable"

let rec GetAccountOfSameCurrency currency =
    let account = UserInteraction.AskAccount()
    if (account.Currency <> currency) then
        Presentation.Error (sprintf "The account selected doesn't match the currency %A" currency)
        GetAccountOfSameCurrency currency
    else
        account

let rec CheckArchivedAccountsAreEmpty(): bool =
    let archivedAccountsInNeedOfAction =
        Account.GetArchivedAccountsWithPositiveBalance None
            |> Async.RunSynchronously
    for archivedAccount,balance in archivedAccountsInNeedOfAction do
        let currency = (archivedAccount:>IAccount).Currency
        Console.WriteLine (sprintf "ALERT! An archived account has received funds:%sAddress: %s Balance: %s%A"
                               Environment.NewLine
                               (archivedAccount:>IAccount).PublicAddress
                               (balance.ToString())
                               currency)
        Console.WriteLine "Please indicate the account you would like to transfer the funds to."
        let account = GetAccountOfSameCurrency currency

        let allBalance = TransferAmount(balance, balance, account.Currency)
        let maybeFee = UserInteraction.AskFee archivedAccount allBalance account.PublicAddress
        match maybeFee with
        | None -> ()
        | Some(feeInfo) ->
            let txId =
                Account.SweepArchivedFunds archivedAccount balance account feeInfo
                    |> Async.RunSynchronously
            Console.WriteLine(sprintf "Transaction successful, its ID is:%s%s" Environment.NewLine txId)
            UserInteraction.PressAnyKeyToContinue ()

    not (archivedAccountsInNeedOfAction.Any())

let private NotReadyReasonToString (reason: Lightning.NotReadyReason): string =
    match reason with
    | Lightning.NotReadyReason.NeedMoreConfirmations (currentConfirmations, neededConfirmations) ->
        sprintf "%d out of %d confirmations" currentConfirmations.Value neededConfirmations.Value
    | Lightning.NotReadyReason.TooOld confirmationsCount ->
        sprintf "channel too old, it has %d confirmations and should already have been opened completely" confirmationsCount.Value

let private CheckChannelStatus (path: string, channelFileId: int): Async<seq<string>> =
    async {
        let! channelStatus = Lightning.LoadChannelAndCheckChannelMessage path
        return
            match channelStatus with
            | Lightning.ChannelStatus.UnusableChannelWithReason (txIdHex, notReadyReason) ->
                let reasonString = NotReadyReasonToString notReadyReason
                let msg =
                    sprintf
                        "Channel %d opening in progress (%s): %s\n"
                        channelFileId reasonString txIdHex
                seq { yield msg }
            | Lightning.ChannelStatus.UsableChannel _ ->
                seq { yield sprintf "Channel %d is open.\n" channelFileId }
    }

let private CheckChannelStatuses(): Async<seq<string>> =
    async {
        let jobs = Lightning.ListSavedChannels () |> Seq.map CheckChannelStatus
        let! statuses = Async.Parallel jobs
        return Seq.collect id statuses
    }

let rec ProgramMainLoop() =
    let accounts = Account.GetAllActiveAccounts()
    let accountAndChannelJob =
        seq {
            yield UserInteraction.DisplayAccountStatuses(WhichAccount.All accounts)
            yield CheckChannelStatuses()
        } |> Async.Parallel
    let results = Async.RunSynchronously accountAndChannelJob
    let accountStatuses: seq<string> = results.[0]
    let channelStatuses: seq<string> = results.[1]
    let lines: seq<string> =
        seq {
            yield! accountStatuses
            yield "\n"
            yield! channelStatuses
        }
    printfn "%s" <| String.concat "\n" lines
    if CheckArchivedAccountsAreEmpty() then
        PerformOperation (accounts.Count())
    ProgramMainLoop()


let NormalStartWithNoParameters () =

    Infrastructure.SetupSentryHook ()

    let exitCode =
        try
            ProgramMainLoop ()
            0
        with
        | ex ->
            Infrastructure.ReportCrash ex
            1

    exitCode


let UpdateServersFile () =
    ServerManager.UpdateServersFile()
    0

let UpdateServersStats () =
    ServerManager.UpdateServersStats()
    0

[<EntryPoint>]
let main argv =
    match argv.Length with
    | 0 ->
        NormalStartWithNoParameters()
    | 1 when argv.[0] = "--update-servers-file" ->
        UpdateServersFile()
    | 1 when argv.[0] = "--update-servers-stats" ->
        UpdateServersStats()
    | _ ->
        failwith "Arguments not recognized"

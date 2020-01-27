namespace GWallet.Backend

open System
open System.Linq
open System.Text
open System.Net
open System.Net.Sockets
open System.Threading

open System
open System.Buffers
open System.Net
open System.Net.Sockets
open System.IO.Pipelines
open System.Text
open System.Threading.Tasks

type ElectrumCommunicationUnsuccessfulException(msg: string, innerException: Exception) =
    inherit Exception(msg, innerException)

type TimeoutOrResult<'T> =
    | Timeout
    | Result of 'T

//lovebitcoin,electrum.emzy,k.emzy,ecdsa
[<AbstractClass>]
type JsonRpcClientNew(resolveHostAsync: unit->Async<IPAddress>, port, timeout: TimeSpan) =
    let minimumBufferSize = 1024

    let logit (x: string) =
        Console.WriteLine x
        Console.Out.Flush()
    let withTimeout (_: TimeSpan) (job: Async<_>) = async {
        //26F 25S
        let timeout = TimeSpan.FromSeconds 50.0
        let read = async {
            let! value = job
            return value |> Result |> Some
        }

        logit("_______gonna setup a timeout of this number of ms: " + timeout.TotalMilliseconds.ToString())
        let delay = async {
            logit("________gonna test timeout")
            let mutable total = int timeout.TotalMilliseconds
            while total > 0 do
                logit ("________wait(left="+total.ToString()+")")
                do! Async.Sleep 5000
                logit("________waited,5sec less")
                total <- total - 5000
            logit(">>>>>TIMEOUT! should jump now")
            return Some Timeout
        }

        let! result = Async.Choice([read; delay])
        match result with
        | Some x ->
            logit "[no timeout]"
            return x
        | None ->
            logit "[a timeout]"
            return Timeout
    }

    let unwrapTimeout timeoutMsg job = async {
        let! maybeRes = job
        match maybeRes with
        | Timeout ->
            let timeoutEx = TimeoutException(timeoutMsg)
            logit(">>>>>TIMEOUT")
            return raise <| ElectrumCommunicationUnsuccessfulException(timeoutMsg, timeoutEx)
        | Result res ->
            return res
    }

    let rec writeToPipeAsync (writer: PipeWriter) (socket: Socket) = async {
        try
            let segment = Array.zeroCreate<byte> minimumBufferSize |> ArraySegment
            logit("_____ writeToPipeAsync: about to start reading/receiving")
            let! read = socket.ReceiveAsync(segment, SocketFlags.None)
                        |> Async.AwaitTask |> withTimeout timeout |> unwrapTimeout "Socket read timed out"
            logit("_____ writeToPipeAsync: read finished")
            match read with
            | 0 ->
                logit("_____ writeToPipeAsync:finished1")
                return writer.Complete()
            | bytesRead ->
                segment.Array.CopyTo(writer.GetMemory(bytesRead))
                writer.Advance bytesRead
                let! flusher = writer.FlushAsync().AsTask() |> Async.AwaitTask
                if flusher.IsCompleted then
                    logit("_____ writeToPipeAsync:finished2")
                    return writer.Complete()
                else
                    logit("_____ writeToPipeAsync:finished3")
                    return! writeToPipeAsync writer socket
        with
        | ex ->
            logit("_____ writeToPipeAsync:finished0")
            return writer.Complete(ex)
    }

    let rec readFromPipeAsync (reader: PipeReader) (state: StringBuilder * int) = async {
        let! result = reader.ReadAsync().AsTask() |> Async.AwaitTask

        let mutable buffer = result.Buffer
        let sb = fst state

        let str = BuffersExtensions.ToArray(& buffer) |> Encoding.UTF8.GetString
        str |> sb.Append |> ignore
        let bracesCount = str |> Seq.sumBy (function | '{' -> 1 | '}' -> -1 | _ -> 0)

        reader.AdvanceTo(buffer.End)

        let braces = (snd state) + bracesCount
        if result.IsCompleted || braces = 0 then
            logit("_____ readFromPipeAsync:finished!")
            return sb.ToString()
        else
            logit("_____ readFromPipeAsync:finished an iteration")
            return! readFromPipeAsync reader (sb, braces)
    }

    let RequestImplAsync (json: string) =
        async {
            let! endpoint = resolveHostAsync() |> withTimeout timeout |> unwrapTimeout "Name resolution timed out"
            logit("_____ resolved")
            use socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            let! _ = socket.ConnectAsync(endpoint, port)
                           |> Async.AwaitTask |> withTimeout timeout |> unwrapTimeout "Socket connect timed out"
            logit("_____ conneected")
            let segment = UTF8Encoding.UTF8.GetBytes(json + Environment.NewLine) |> ArraySegment

            let! _ = socket.SendAsync(segment, SocketFlags.None)
                        |> Async.AwaitTask |> withTimeout timeout |> unwrapTimeout "Socket send timed out"
            logit("_____ sent")
            let pipe = Pipe()

            let! _ = writeToPipeAsync pipe.Writer socket |> Async.StartChild
            return! readFromPipeAsync pipe.Reader (StringBuilder(), 0)
        }

    abstract member RequestAsync: string -> Async<string>
    abstract member RequestAsyncAsTask: string -> Task<string>

    default __.RequestAsync (json: string) =
        async {
            try
                let! res = RequestImplAsync json
                Console.WriteLine "=======request finished successfully"
                return res
            with
            | :? AggregateException as ae when ae.Flatten().InnerExceptions
                    |> Seq.exists (fun x -> x :? SocketException ||
                                            x :? TimeoutException ||
                                            x :? ElectrumCommunicationUnsuccessfulException) ->
                logit("EXCEPTION!")
                return raise <| ElectrumCommunicationUnsuccessfulException(ae.Message, ae)
            | :? SocketException as ex ->
                logit("EXCEPTION!!")
                return raise <| ElectrumCommunicationUnsuccessfulException(ex.Message, ex)
        }

    default self.RequestAsyncAsTask (json: string) =
        self.RequestAsync json |> Async.StartAsTask


module JsonRpcSharpOld =

    exception ServerUnresponsiveException
    exception NoResponseReceivedAfterRequestException

    type LegacyTcpClient  (resolveHostAsync: unit->Async<IPAddress>, port: uint32) =
        let rec WrapResult (acc: List<byte>): string =
            let reverse = List.rev acc
            Encoding.UTF8.GetString(reverse.ToArray())

        let rec ReadByte (stream: NetworkStream): Option<byte> =
            let byteInt = stream.ReadByte()
            if (byteInt = -1) then
                None
            else
                Some(Convert.ToByte(byteInt))

        let DEFAULT_TIMEOUT_FOR_FIRST_DATA_AVAILABLE_SIGNAL_TO_HAPPEN = Config.DEFAULT_NETWORK_TIMEOUT
        let DEFAULT_TIMEOUT_FOR_SUBSEQUENT_DATA_AVAILABLE_SIGNAL_TO_HAPPEN = TimeSpan.FromMilliseconds(500.0)
        let DEFAULT_TIME_TO_WAIT_BETWEEN_DATA_GAPS = TimeSpan.FromMilliseconds(1.0)
        let rec ReadInternal (stream: NetworkStream) acc (initTime: DateTime): string =
            let timeIsUp (): bool =
                if (List.Empty = acc) then
                    if (DateTime.UtcNow > initTime + DEFAULT_TIMEOUT_FOR_FIRST_DATA_AVAILABLE_SIGNAL_TO_HAPPEN) then
                        raise NoResponseReceivedAfterRequestException
                    else
                        false
                else
                    (DateTime.UtcNow > initTime + DEFAULT_TIMEOUT_FOR_SUBSEQUENT_DATA_AVAILABLE_SIGNAL_TO_HAPPEN)

            if (not (stream.DataAvailable)) || (not (stream.CanRead)) then
                if (timeIsUp()) then
                    WrapResult acc
                else
                    Thread.Sleep(DEFAULT_TIME_TO_WAIT_BETWEEN_DATA_GAPS)
                    ReadInternal stream acc initTime
            else
                match ReadByte stream with
                | None -> WrapResult acc
                | Some(byte) ->
                    ReadInternal stream (byte::acc) DateTime.UtcNow

        let Read (stream: NetworkStream): string =
            ReadInternal stream List.Empty DateTime.UtcNow

        let Connect(): System.Net.Sockets.TcpClient =

            let host = resolveHostAsync() |> Async.RunSynchronously

            let tcpClient = new System.Net.Sockets.TcpClient(host.AddressFamily)
            tcpClient.SendTimeout <- Convert.ToInt32 Config.DEFAULT_NETWORK_TIMEOUT.TotalMilliseconds
            tcpClient.ReceiveTimeout <- Convert.ToInt32 Config.DEFAULT_NETWORK_TIMEOUT.TotalMilliseconds

            let connectTask = tcpClient.ConnectAsync(host, int port)

            if not (connectTask.Wait(Config.DEFAULT_NETWORK_TIMEOUT)) then
                raise ServerUnresponsiveException
            tcpClient

        new(host: IPAddress, port: uint32) = new LegacyTcpClient((fun () -> async { return host }), port)

        member self.Request (request: string): Async<string> = async {
            use tcpClient = Connect()
            let stream = tcpClient.GetStream()
            if not stream.CanTimeout then
                failwith "Inner NetworkStream should allow to set Read/Write Timeouts"
            stream.ReadTimeout <- Convert.ToInt32 Config.DEFAULT_NETWORK_TIMEOUT.TotalMilliseconds
            stream.WriteTimeout <- Convert.ToInt32 Config.DEFAULT_NETWORK_TIMEOUT.TotalMilliseconds
            let bytes = Encoding.UTF8.GetBytes(request + "\n");
            stream.Write(bytes, 0, bytes.Length)
            stream.Flush()
            return Read stream
        }
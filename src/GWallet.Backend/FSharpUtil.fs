﻿namespace GWallet.Backend

open System
open System.Threading.Tasks
open System.Runtime.ExceptionServices
open Microsoft.FSharp.Reflection

module FSharpUtil =

    type TimeoutOrResult<'T> =
        | Timeout
        | Result of 'T

    type internal ResultWrapper<'T>(value : 'T) =

        // hack?
        inherit Exception()

        member self.Value = value

    module AsyncExtensions =

        let MixedParallel2 (a: Async<'T1>) (b: Async<'T2>): Async<'T1*'T2> =
            async {
                let aJob = Async.StartChild a
                let bJob = Async.StartChild b

                let! aStartedJob = aJob
                let! bStartedJob = bJob

                let! aJobResult = aStartedJob
                let! bJobResult = bStartedJob

                return aJobResult,bJobResult
            }

        let MixedParallel3 (a: Async<'T1>) (b: Async<'T2>) (c: Async<'T3>): Async<'T1*'T2*'T3> =
            async {
                let aJob = Async.StartChild a
                let bJob = Async.StartChild b
                let cJob = Async.StartChild c

                let! aStartedJob = aJob
                let! bStartedJob = bJob
                let! cStartedJob = cJob

                let! aJobResult = aStartedJob
                let! bJobResult = bStartedJob
                let! cJobResult = cStartedJob

                return aJobResult,bJobResult,cJobResult
            }

        // efficient raise
        let private RaiseResult (e: ResultWrapper<'T>) =
            Async.FromContinuations(fun (_, econt, _) -> econt e)

        // like Async.Choice, but with no need for Option<T> types
        let WhenAny<'T>(jobs: seq<Async<'T>>): Async<'T> =
            let wrap (job: Async<'T>): Async<Option<'T>> =
                async {
                    let! res = job
                    return Some res
                }

            async {
                let wrappedJobs = jobs |> Seq.map wrap
                let! combinedRes = Async.Choice wrappedJobs
                match combinedRes with
                | Some x ->
                    return x
                | None ->
                    return failwith "unreachable"
            }

        // a mix between Async.WhenAny and Async.Choice
        let WhenAnyAndAll<'T>(jobs: seq<Async<'T>>): Async<Async<array<'T>>> =
            let taskSource = TaskCompletionSource<unit>()
            let wrap (job: Async<'T>) =
                async {
                    let! res = job
                    taskSource.TrySetResult() |> ignore
                    return res
                }
            async {
                let allJobsInParallel =
                    jobs
                        |> Seq.map wrap
                        |> Async.Parallel
                        |> Async.StartChild
                let! allJobsStarted = allJobsInParallel
                let! _ = Async.AwaitTask taskSource.Task
                return allJobsStarted
            }

    let rec private ListIntersectInternal list1 list2 offset acc currentIndex =
        match list1,list2 with
        | [],[] -> List.rev acc
        | [],_ -> List.append (List.rev acc) list2
        | _,[] -> List.append (List.rev acc) list1
        | head1::tail1,head2::tail2 ->
            if currentIndex % (int offset) = 0 then
                ListIntersectInternal list1 tail2 offset (head2::acc) (currentIndex + 1)
            else
                ListIntersectInternal tail1 list2 offset (head1::acc) (currentIndex + 1)

    let ListIntersect<'T> (list1: List<'T>) (list2: List<'T>) (offset: uint32): List<'T> =
        ListIntersectInternal list1 list2 offset [] 1

    let withTimeout (timeSpan: TimeSpan) (operation: Async<'R>): Async<Option<'R>> = async {
        let! child = Async.StartChild (operation, int timeSpan.TotalMilliseconds)
        try
            let! result = child
            return Some result
        with :? TimeoutException ->
            return None
    }

    let WithTimeout (timeSpan: TimeSpan) (job: Async<'T>): Async<Option<'T>> = async {

        let read = async {
            let! value = job
            return value |> Result |> Some
        }

        let delay = async {
            let total = int timeSpan.TotalMilliseconds
            do! Async.Sleep total
            return Some Timeout
        }

        let! result = Async.Choice([read; delay])
        match result with
        | Some x ->
            match x with
            | Result r ->
                return Some r
            | Timeout ->
                return None
        | None ->
            // none of the jobs passed to Async.Choice returns None
            return failwith "unreachable"
    }

    // FIXME: we should not need this workaround anymore when this gets addressed:
    //        https://github.com/fsharp/fslang-suggestions/issues/660
    let ReRaise (ex: Exception): Exception =
        (ExceptionDispatchInfo.Capture ex).Throw ()
        failwith "Should be unreachable"
        ex

    let rec public FindException<'T when 'T:> Exception>(ex: Exception): Option<'T> =
        let rec findExInSeq(sq: seq<Exception>) =
            match Seq.tryHead sq with
            | Some head ->
                let found = FindException head
                match found with
                | Some ex -> Some ex
                | None ->
                    findExInSeq <| Seq.tail sq
            | None ->
                None
        if null = ex then
            None
        else
            match ex with
            | :? 'T as specificEx -> Some(specificEx)
            | :? AggregateException as aggEx ->
                findExInSeq aggEx.InnerExceptions
            | _ -> FindException<'T>(ex.InnerException)

// http://stackoverflow.com/a/28466431/6503091
    // will crash if 'T contains members which aren't only tags
    let Construct<'T> (caseInfo: UnionCaseInfo) = FSharpValue.MakeUnion(caseInfo, [||]) :?> 'T

    let GetUnionCaseInfoAndInstance<'T> (caseInfo: UnionCaseInfo) = (Construct<'T> caseInfo)

    let GetAllElementsFromDiscriminatedUnion<'T>() =
        FSharpType.GetUnionCases(typeof<'T>)
        |> Seq.map GetUnionCaseInfoAndInstance<'T>

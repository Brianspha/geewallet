﻿namespace GWallet.Backend.Tests

open System

open NUnit.Framework

open GWallet.Backend

module FaultTolerance =

    [<Test>]
    let ``can retrieve basic T for single func``() =
        let someStringArg = "foo"
        let someResult = 1
        let func (arg: string) =
            someResult
        let dataRetreived =
            FaultTolerantClient.Query<string,int> someStringArg [ func ]
        Assert.That(dataRetreived, Is.TypeOf<int>())
        Assert.That(dataRetreived, Is.EqualTo(someResult))

    [<Test>]
    let ``can retrieve basic T for 2 funcs``() =
        let someStringArg = "foo"
        let someResult = 1
        let func1 (arg: string) =
            someResult
        let func2 (arg: string) =
            someResult
        let dataRetreived = FaultTolerantClient.Query<string,int> someStringArg [ func1; func2 ]
        Assert.That(dataRetreived, Is.TypeOf<int>())
        Assert.That(dataRetreived, Is.EqualTo(someResult))

    [<Test>]
    let ``throws NotAvailable if no funcs``(): unit =
        Assert.Throws<FaultTolerantClient.NotAvailable>(
            fun _ -> FaultTolerantClient.Query<string,int> "_" [] |> ignore
        ) |> ignore

    [<Test>]
    let ``can retrieve one if 1 of the funcs throws``() =
        let someStringArg = "foo"
        let someResult = 1
        let func1 (arg: string) =
            failwith "boo"
        let func2 (arg: string) =
            someResult
        let dataRetreived =
            FaultTolerantClient.Query<string,int> someStringArg [ func1; func2 ]
        Assert.That(dataRetreived, Is.TypeOf<int>())
        Assert.That(dataRetreived, Is.EqualTo(someResult))
﻿namespace GWallet.Frontend.XF

open System.Linq

open Xamarin.Forms

open GWallet.Backend

module Initialization =

    let private GlobalInit () =
        Infrastructure.SetupSentryHook ()

    let internal LandingPage(): NavigationPage =
        GlobalInit ()

        let accounts = Account.GetAllActiveAccounts()
        if not (accounts.Any()) then
            let welcomePage = WelcomePage()
            let navPage = NavigationPage welcomePage
            NavigationPage.SetHasNavigationBar(welcomePage, false)
            navPage
        else
            let balancesPage = BalancesPage()
            let navPage = NavigationPage(balancesPage)
            NavigationPage.SetHasNavigationBar(balancesPage, false)
            navPage

type App() =
    inherit Application(MainPage = Initialization.LandingPage())
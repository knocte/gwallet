﻿namespace GWallet.Frontend.XF.Android

open System

open Android.App
open Android.Content
open Android.Content.PM
open Android.Runtime
open Android.Views
open Android.Widget
open Android.OS
open Xamarin.Forms.Platform.Android

type Resources = GWallet.Frontend.XF.Android.Resource

[<Activity (LaunchMode = LaunchMode.SingleTask, 
            Label = "geewallet", 
            Icon = "@drawable/icon", 
            Theme = "@style/MyTheme", 
            MainLauncher = true, 
            ConfigurationChanges = (ConfigChanges.ScreenSize ||| ConfigChanges.Orientation))>]
type MainActivity() =
    inherit FormsAppCompatActivity()

    override this.OnCreate (bundle: Bundle) =
        FormsAppCompatActivity.TabLayoutResource <- Resources.Layout.Tabbar
        FormsAppCompatActivity.ToolbarResource <- Resources.Layout.Toolbar

        base.OnCreate (bundle)

        Xamarin.Forms.Forms.Init (this, bundle)

        this.LoadApplication (new GWallet.Frontend.XF.App ())

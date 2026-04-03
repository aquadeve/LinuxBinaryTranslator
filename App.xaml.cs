// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.

using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using System.Diagnostics;

namespace LinuxBinaryTranslator
{
    /// <summary>
    /// UWP application entry point.
    /// Uses Dark theme for better terminal readability on Xbox One.
    /// </summary>
    sealed partial class App : Application
    {
        public App()
        {
            Debug.WriteLine("[LBT] App ctor: begin");
            this.InitializeComponent();
            Debug.WriteLine("[LBT] App ctor: InitializeComponent complete");
            this.Suspending += OnSuspending;
            this.UnhandledException += OnUnhandledException;

            // Xbox One: require focus engagement for better gamepad UX
            this.RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
            Debug.WriteLine("[LBT] App ctor: complete");
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Debug.WriteLine("[LBT] OnLaunched: begin");
            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                Debug.WriteLine("[LBT] OnLaunched: creating root frame");
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    Debug.WriteLine("[LBT] OnLaunched: navigating to MainPage");
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                    Debug.WriteLine("[LBT] OnLaunched: navigation returned");
                }
                Debug.WriteLine("[LBT] OnLaunched: activating window");
                Window.Current.Activate();
            }
            Debug.WriteLine("[LBT] OnLaunched: complete");
        }

        protected override void OnFileActivated(FileActivatedEventArgs args)
        {
            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }
            rootFrame.Navigate(typeof(MainPage), args);
            Window.Current.Activate();
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            deferral.Complete();
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Debug.WriteLine("[LBT] Unhandled exception: " + e.Exception);

#if DEBUG
            // Keep the app alive under the debugger long enough to surface the
            // actual exception instead of terminating with a generic fail-fast.
            e.Handled = true;
#endif
        }
    }
}

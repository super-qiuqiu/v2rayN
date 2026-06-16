using v2rayN.Desktop.Common;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (!Design.IsDesignMode)
            {
                AppManager.Instance.InitComponents();
                DataContext = StatusBarViewModel.Instance;
            }

            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            if (OperatingSystem.IsMacOS())
            {
                Current?.TryGetFeature<IActivatableLifetime>()?.Activated += OnMacOSActivated;
            }

            // Auto quit GUI after core starts if --auto-quit-gui flag is set
            if (Program.AutoQuitGui)
            {
                _ = Task.Run(AutoQuitGuiAfterCoreStart);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    #region MacOS Activation

    private void OnMacOSActivated(object? sender, ActivatedEventArgs args)
    {
        if (args.Kind != ActivationKind.Reopen)
        {
            return;
        }

        if ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not MainWindow mainWindow)
        {
            return;
        }

        var isMiniaturized = MacAppUtils.IsWindowMiniaturized(mainWindow);

        Dispatcher.UIThread.Post(() =>
        {
            if (isMiniaturized)
            {
                RestoreMacOSAccessoryPolicyAfterMiniaturize(mainWindow);
                mainWindow.ShowHideWindow(true);
                return;
            }

            if (!AppManager.Instance.Config.UiItem.MacOSShowInDock)
            {
                MacAppUtils.SetActivationPolicyAccessory();
            }

            mainWindow.ShowHideWindow(true);
        });
    }

    private static void RestoreMacOSAccessoryPolicyAfterMiniaturize(MainWindow mainWindow)
    {
        if (AppManager.Instance.Config.UiItem.MacOSShowInDock)
        {
            return;
        }

        mainWindow
            .GetObservable(Window.WindowStateProperty)
            .Skip(1)
            .Where(state => state != WindowState.Minimized)
            .Take(1)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => QueueMacOSAccessoryPolicyRestore(mainWindow));
    }

    private static void QueueMacOSAccessoryPolicyRestore(MainWindow mainWindow)
    {
        // AppKit may keep isMiniaturized set until the Dock restore animation finishes.
        DispatcherTimer.RunOnce(() => RestoreMacOSAccessoryPolicy(mainWindow), TimeSpan.FromMilliseconds(300));
    }

    private static void RestoreMacOSAccessoryPolicy(MainWindow mainWindow)
    {
        if (AppManager.Instance.Config.UiItem.MacOSShowInDock || MacAppUtils.IsWindowMiniaturized(mainWindow))
        {
            return;
        }

        MacAppUtils.SetActivationPolicyAccessory();
        mainWindow.Activate();
        mainWindow.Focus();
    }

    #endregion MacOS Activation

    #region App Event

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject != null)
        {
            Logging.SaveLog("CurrentDomain_UnhandledException", (Exception)e.ExceptionObject);
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logging.SaveLog("TaskScheduler_UnobservedTaskException", e.Exception);
    }

    private async void MenuAddServerViaClipboardClick(object? sender, EventArgs e)
    {
        try
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: not null })
            {
                AppEvents.AddServerViaClipboardRequested.Publish();
                await Task.Delay(1000);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("MenuAddServerViaClipboardClick", ex);
        }
    }

    private async void MenuExit_Click(object? sender, EventArgs e)
    {
        await AppManager.Instance.AppExitAsync(false);
        AppManager.Instance.Shutdown(true);
    }

    private async void MenuExitKeepCore_Click(object? sender, EventArgs e)
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow mainWindow }
            && await UI.ShowYesNo(mainWindow, ResUI.menuExitKeepCoreTips) != ButtonResult.Yes)
        {
            return;
        }

        if (await AppManager.Instance.AppExitAsync(false, keepCore: true))
        {
            AppManager.Instance.Shutdown(true);
            return;
        }

        NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
    }

    private async Task AutoQuitGuiAfterCoreStart()
    {
        try
        {
            Logging.SaveLog("AutoQuitGui: Waiting for core to start...");

            // Wait for core to start (check every second, up to 30 seconds)
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(1000);

                if (CoreManager.Instance.HasRunningCore)
                {
                    Logging.SaveLog($"AutoQuitGui: Core detected running after {i + 1} seconds, exiting GUI...");

                    // Wait a bit more to ensure core is stable
                    await Task.Delay(2000);

                    await RunOnUiThreadAsync(ExitGuiKeepCoreAsync);
                    return;
                }
            }

            Logging.SaveLog("AutoQuitGui: Timeout waiting for core to start, GUI will remain open");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AutoQuitGui error", ex);
        }
    }

    private static async Task ExitGuiKeepCoreAsync()
    {
        if (await AppManager.Instance.AppExitAsync(false, keepCore: true))
        {
            Logging.SaveLog("AutoQuitGui: Successfully exited GUI, core still running");
            AppManager.Instance.Shutdown(false);
            return;
        }

        Logging.SaveLog("AutoQuitGui: Failed to exit while keeping core");
    }

    private static async Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            await action();
            return;
        }

        var completion = new TaskCompletionSource<bool>();

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await action();
                completion.SetResult(true);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        await completion.Task;
    }

    #endregion App Event
}

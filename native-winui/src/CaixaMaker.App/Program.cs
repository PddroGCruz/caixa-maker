using CaixaMaker.App.Interop;
using CaixaMaker.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace CaixaMaker.App;

/// <summary>
/// Ponto de entrada explícito para garantir instância única nativa no Windows via AppLifecycle
/// antes da inicialização do loop WinUI 3 (substituindo tauri-plugin-single-instance).
/// </summary>
public static class Program
{
    public const string SingleInstanceKey = "CaixaMaker_Desktop_SingleInstance";

    public static bool IsSmokeTestMode { get; private set; }
    public static bool IsCaptureScreenshotsMode { get; private set; }
    public static string? ScreenshotOutputDirectory { get; private set; }
    public static string? StartupProjectFilePath { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        IsSmokeTestMode = args.Any(a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase));
        ScreenshotOutputDirectory = ParseScreenshotOutputArg(args);
        IsCaptureScreenshotsMode = !string.IsNullOrWhiteSpace(ScreenshotOutputDirectory);
        StartupProjectFilePath = ProjectLaunchHelper.ExtractProjectFilePath(args);

        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (!IsSmokeTestMode && !IsCaptureScreenshotsMode && DecideRedirection())
        {
            return 0;
        }

        Application.Start(callbackParams =>
        {
            _ = callbackParams;
            DispatcherQueueSynchronizationContext context =
                new(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    private static bool DecideRedirection()
    {
        AppActivationArguments activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        StartupProjectFilePath ??= ExtractProjectFilePathFromActivation(activationArgs);
        AppInstance keyInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);

        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += OnKeyInstanceActivated;
            return false;
        }

        RedirectActivationTo(activationArgs, keyInstance);
        return true;
    }

    private static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
    {
        try
        {
            _ = NativeWindowHelper.AllowSetForegroundWindow(unchecked((int)keyInstance.ProcessId));
        }
        catch
        {
            // Permissão de foco é melhor-esforço antes do redirecionamento.
        }

        using AutoResetEvent completionEvent = new(initialState: false);
        _ = Task.Run(() =>
        {
            try
            {
                keyInstance.RedirectActivationToAsync(args).AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Ignora erro de redirecionamento caso a instância primária já esteja em encerramento.
            }
            finally
            {
                completionEvent.Set();
            }
        });

        completionEvent.WaitOne(TimeSpan.FromSeconds(5));
    }

    private static string? ParseScreenshotOutputArg(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--capture-ui-screenshots", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    return Path.GetFullPath(args[i + 1].Trim().Trim('"'));
                }

                return Path.GetFullPath(Path.Combine("artifacts", "screenshots"));
            }

            const string prefix = "--capture-ui-screenshots=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string raw = arg.Substring(prefix.Length).Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return Path.GetFullPath(raw);
                }
            }
        }

        return null;
    }

    private static void OnKeyInstanceActivated(object? sender, AppActivationArguments args)
    {
        string? projectPath = ExtractProjectFilePathFromActivation(args);
        App.NotifyExternalActivation(projectPath);
    }

    private static string? ExtractProjectFilePathFromActivation(AppActivationArguments? args)
    {
        if (args is null)
        {
            return null;
        }

        try
        {
            if (args.Kind == ExtendedActivationKind.File
                && args.Data is IFileActivatedEventArgs fileArgs
                && fileArgs.Files.Count > 0)
            {
                foreach (IStorageItem item in fileArgs.Files)
                {
                    if (!string.IsNullOrWhiteSpace(item.Path)
                        && ProjectLaunchHelper.IsSupportedProjectExtension(item.Path))
                    {
                        return item.Path;
                    }
                }
            }

            if (args.Kind == ExtendedActivationKind.Launch
                && args.Data is ILaunchActivatedEventArgs launchArgs
                && !string.IsNullOrWhiteSpace(launchArgs.Arguments))
            {
                return ProjectLaunchHelper.ExtractProjectFilePathFromCommandLine(launchArgs.Arguments);
            }
        }
        catch
        {
            // Ignora payload de ativação malformado para não interromper o foco da janela.
        }

        return null;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Papery.Services;
using Windows.ApplicationModel.Activation;

namespace Papery;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved background error", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        AppServices.Initialize();

        // Files sent from a second launch (double-clicking another PDF) arrive here.
        AppInstance.GetCurrent().Activated += OnRedirectedActivation;

        var window = WindowManager.Create();
        window.Activate();

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var files = FilesFrom(activation);
        if (files.Count == 0)
        {
            // Launched through the "papery.exe" alias or a script: the paths are plain command-line arguments.
            files = Environment.GetCommandLineArgs().Skip(1)
                .Where(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
                .Select(Path.GetFullPath).ToList();
        }

        Log.Info($"Started ({activation.Kind}), {files.Count} file(s) to open");
        if (files.Count > 0)
        {
            _ = window.OpenFilesAsync(files);
        }
    }

    private static void OnRedirectedActivation(object? sender, AppActivationArguments args)
    {
        var files = FilesFrom(args);
        var window = WindowManager.Active ?? (WindowManager.Count > 0 ? WindowManager.All[0] : null);
        window?.DispatcherQueue.TryEnqueue(() =>
        {
            window.BringToFront();
            if (files.Count > 0)
            {
                _ = window.OpenFilesAsync(files);
            }
        });
    }

    /// <summary>File paths from a double-click (file activation) or the command line.</summary>
    private static List<string> FilesFrom(AppActivationArguments args)
    {
        try
        {
            if (args.Kind == ExtendedActivationKind.File && args.Data is IFileActivatedEventArgs fileArgs)
            {
                return fileArgs.Files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            }

            if (args.Kind == ExtendedActivationKind.Launch && args.Data is ILaunchActivatedEventArgs launch)
            {
                return CommandLineFiles(launch.Arguments);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Reading activation arguments failed", ex);
        }

        return [];
    }

    private static List<string> CommandLineFiles(string? commandLine)
    {
        var files = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return files;
        }

        // Split on spaces, keeping "quoted paths with spaces" together.
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in commandLine + ' ')
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ' ' && !quoted)
            {
                string token = current.ToString().Trim();
                if (token.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(token))
                {
                    files.Add(Path.GetFullPath(token));
                }

                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        return files;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error", e.Exception);

        // A broken PDF or an unexpected glitch should never take the user's other documents down
        // with it. Problems inside the PDF engine are already turned into messages; this is the last net.
        e.Handled = true;
    }
}

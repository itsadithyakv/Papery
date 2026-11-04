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
        _ = Task.Run(TempFiles.SweepOld);

        // Files sent from a second launch (double-clicking another PDF) arrive here, on a background thread.
        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        AppInstance.GetCurrent().Activated += OnRedirectedActivation;

        var window = WindowManager.Create();
        window.Activate();

        // Pro already works from the local record; this just confirms with the Store in the background.
        AppServices.StartLicenseChecks(window.Hwnd);

        // Reading how Papery was started can fail (the program that asked has gone away); start empty rather than not at all.
        AppActivationArguments? activation = null;
        try
        {
            activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Reading how Papery was started failed", ex);
        }

        var files = activation == null ? [] : FilesFrom(activation);
        if (files.Count == 0)
        {
            // Launched through the "papery.exe" alias or a script: the paths are plain command-line arguments.
            files = Environment.GetCommandLineArgs().Skip(1)
                .Where(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
                .Select(Path.GetFullPath).ToList();
        }

        bool merge = (activation != null && WantsMerge(activation)) || Environment.GetCommandLineArgs().Contains(MergeSwitch, StringComparer.OrdinalIgnoreCase);
        Log.Info($"Started ({activation?.Kind}), {files.Count} file(s) to {(merge ? "merge" : "open")}");
        _ = StartAsync(window, files, merge, activation == null ? null : VerbOf(activation));
    }

    /// <summary>Last time's PDFs first, then whatever this launch was for (on top), then any unsaved work to bring back.</summary>
    private static async Task StartAsync(MainWindow window, List<string> files, bool merge, string? verb)
    {
        try
        {
            await SessionService.RestoreAsync(window);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Reopening last time's PDFs failed", ex);
        }

        Open(window, files, merge, verb);
        await window.OfferRecoveryAsync();
    }

    /// <summary>"papery.exe --merge a.pdf b.pdf" opens a Merge tab (used by Explorer's "Merge with Papery").</summary>
    private const string MergeSwitch = "--merge";

    /// <summary>Which right-click item in File Explorer started Papery, if any.</summary>
    private static string? VerbOf(AppActivationArguments args) =>
        args.Kind == ExtendedActivationKind.File && args.Data is IFileActivatedEventArgs file ? file.Verb : null;

    private static void Open(MainWindow window, List<string> files, bool merge, string? verb = null)
    {
        if (merge || verb == "merge")
        {
            window.ShowMerge(files);
        }
        else if (verb == "combine")
        {
            _ = window.MakeNewPdfAsync("images-to-pdf", files);
        }
        else if (verb == "compress" && files.Count > 0)
        {
            _ = window.OpenFilesAsync(files, Commands.ToolCatalog.All.First(t => t.Key == "compress"));
        }
        else if (verb == "convert" && files.Count > 0)
        {
            _ = window.OpenFilesAsync(files, Commands.ToolCatalog.All.First(t => t.Key == "pdf-to-images"));
        }
        else if (files.Count > 0)
        {
            _ = window.OpenFilesAsync(files);
        }
    }

    private static bool WantsMerge(AppActivationArguments args) =>
        args.Kind == ExtendedActivationKind.Launch && args.Data is ILaunchActivatedEventArgs launch
        && (launch.Arguments ?? string.Empty).Contains(MergeSwitch, StringComparison.OrdinalIgnoreCase);

    private static Microsoft.UI.Dispatching.DispatcherQueue? _ui;

    /// <summary>
    /// Windows calls this on a background thread. Nothing here may touch a window (WinUI windows only answer on their
    /// own thread), and nothing may throw: an error escaping back into Windows' app-launch code ends Papery on the
    /// spot, open files and all. So the files are read here and everything else happens on the window thread.
    /// </summary>
    private static void OnRedirectedActivation(object? sender, AppActivationArguments args)
    {
        try
        {
            var files = FilesFrom(args);
            bool merge = WantsMerge(args);
            string? verb = VerbOf(args);
            Log.Info($"Handed {files.Count} file(s) by another launch");
            _ui?.TryEnqueue(() =>
            {
                try
                {
                    var window = WindowManager.ForNewFiles();
                    if (window == null)
                    {
                        window = WindowManager.Create();
                        window.Activate();
                    }

                    window.BringToFront();
                    Open(window, files, merge, verb);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Error("Opening handed-over files failed", ex);
                }
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Reading a handed-over launch failed", ex);
        }
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

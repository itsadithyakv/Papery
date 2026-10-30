using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Papery.Pdf.Editing;

/// <summary>Runs the bundled QPDF. It works only on local files and never touches the network.</summary>
internal static class QpdfTool
{
    private const int Success = 0;
    private const int SuccessWithWarnings = 3;

    private static string ExePath => Path.Combine(AppContext.BaseDirectory, "qpdf", "qpdf.exe");

    /// <summary>A job for a file. Warnings aren't failures, because the caller always checks the result.</summary>
    public static JsonObject Job(string inputPath, string outputPath, string? password)
    {
        var job = new JsonObject { ["inputFile"] = inputPath, ["outputFile"] = outputPath, ["warningExit0"] = string.Empty };
        if (password != null)
        {
            job["password"] = password;
        }

        return job;
    }

    /// <summary>Asks for the tightest file that looks exactly the same.</summary>
    public static JsonObject Tight(JsonObject job)
    {
        job["objectStreams"] = "generate";
        job["compressStreams"] = "y";
        job["recompressFlate"] = string.Empty;
        job["compressionLevel"] = "9";
        job["removeUnreferencedResources"] = "yes";
        return job;
    }

    /// <summary>
    /// Runs a job. It's handed over in a short-lived file in the user's own temp folder, rather
    /// than on the command line, so passwords never show up in the list of running processes.
    /// </summary>
    public static async Task RunAsync(JsonObject job, string errorPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Papery", "jobs")).FullName;
        string jobFile = Path.Combine(folder, $"{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(jobFile, job.ToJsonString(), new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await RunFileAsync(jobFile, errorPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(jobFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // It's in the temp folder; Windows clears it in time.
            }
        }
    }

    private static async Task RunFileAsync(string jobFile, string errorPath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add($"--job-json-file={jobFile}");

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("QPDF did not start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new PdfEditException(PdfEditError.BuildFailed, errorPath, ex);
        }

        using (process)
        {
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var ignored = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            await Task.WhenAll(errors, ignored).ConfigureAwait(false);
            if (process.ExitCode is not (Success or SuccessWithWarnings))
            {
                throw new PdfEditException(PdfEditError.BuildFailed, errorPath, new InvalidOperationException($"QPDF exit code {process.ExitCode}"));
            }
        }
    }
}

using System.Diagnostics;

namespace Papery.Pdf.Editing;

/// <summary>Runs the bundled QPDF. It works only on local files and never touches the network.</summary>
internal static class QpdfTool
{
    private const int Success = 0;
    private const int SuccessWithWarnings = 3;

    private static string ExePath => Path.Combine(AppContext.BaseDirectory, "qpdf", "qpdf.exe");

    /// <summary>Rewrites a PDF as tightly as it can without changing how anything looks. The result still gets checked by the caller.</summary>
    public static async Task ShrinkAsync(string inputPath, string outputPath, string? password, string errorPath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        // The password goes over stdin so it isn't visible in the process list.
        if (password != null)
        {
            start.ArgumentList.Add("--password-file=-");
        }

        foreach (string option in new[] { "--object-streams=generate", "--compress-streams=y", "--recompress-flate", "--compression-level=9", "--remove-unreferenced-resources=yes" })
        {
            start.ArgumentList.Add(option);
        }

        start.ArgumentList.Add("--");
        start.ArgumentList.Add(inputPath);
        start.ArgumentList.Add(outputPath);

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
            if (password != null)
            {
                await process.StandardInput.WriteLineAsync(password).ConfigureAwait(false);
            }

            process.StandardInput.Close();
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

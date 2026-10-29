using System.Diagnostics;

namespace Papery.Services;

/// <summary>Small hand-offs to Windows itself.</summary>
internal static class Shell
{
    /// <summary>Opens File Explorer with the file selected.</summary>
    public static void ShowInFolder(string path)
    {
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
    }
}

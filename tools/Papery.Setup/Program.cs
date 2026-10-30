using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

// Installs Papery for testing on this PC: trusts the test certificate, then installs the app and what it needs.
// Add --check to only confirm that everything is packed inside, changing nothing.
const string PackageName = "Papery.Dev";

Console.Title = "Papery Setup";
Console.WriteLine("Papery Setup");
Console.WriteLine();
string work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PaperySetup", Guid.NewGuid().ToString("N"))).FullName;
int result = 1;
try
{
    string msix = Unpack("Papery.msix", work);
    string cer = Unpack("Papery.cer", work);
    string runtime = Unpack("Runtime.msix", work);
    if (args.Contains("--check"))
    {
        Console.WriteLine($"Everything's here: {new FileInfo(msix).Length / (1024 * 1024)} MB app, {new FileInfo(runtime).Length / (1024 * 1024)} MB runtime, certificate {new FileInfo(cer).Length} bytes.");
        return 0;
    }

    Console.WriteLine("Trusting the test certificate on this PC...");
    using (var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine))
    {
        store.Open(OpenFlags.ReadWrite);
        store.Add(X509CertificateLoader.LoadCertificateFromFile(cer));
    }

    Console.WriteLine("Installing Papery. This can take a minute...");
    string install = $"Add-AppxPackage -Path '{msix}' -DependencyPath '{runtime}' -ForceApplicationShutdown -ForceUpdateFromAnyVersion";
    if (PowerShell(install) != 0)
    {
        // A developer copy of the same app can block this. Only that copy is removed.
        Console.WriteLine("Replacing an earlier copy of Papery...");
        _ = PowerShell($"Get-AppxPackage {PackageName} | Remove-AppxPackage");
        if (PowerShell(install) != 0)
        {
            throw new InvalidOperationException("Windows wouldn't install the package. The message above says why.");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Papery is installed. You'll find it in the Start menu. Opening it now...");
    _ = PowerShell($"Start-Process (\"shell:AppsFolder\\\" + (Get-AppxPackage {PackageName}).PackageFamilyName + \"!App\")");
    Thread.Sleep(2000);
    result = 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
{
    Console.WriteLine();
    Console.WriteLine("Papery couldn't be installed: " + ex.Message);
    Console.WriteLine("Press Enter to close.");
    Console.ReadLine();
}
finally
{
    try
    {
        Directory.Delete(work, recursive: true);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // Windows clears its temp folder in time.
    }
}

return result;

static string Unpack(string name, string folder)
{
    using var from = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"This installer is missing {name}.");
    string path = Path.Combine(folder, name);
    using var to = File.Create(path);
    from.CopyTo(to);
    return path;
}

static int PowerShell(string command)
{
    var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false };
    foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command })
    {
        start.ArgumentList.Add(arg);
    }

    using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell didn't start.");
    process.WaitForExit();
    return process.ExitCode;
}

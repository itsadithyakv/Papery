// Papery test file generator.
//
//   dotnet run --project tools/Papery.TestFiles -c Release                  everything (~350 MB, a few minutes)
//   dotnet run --project tools/Papery.TestFiles -c Release -- --skip-large   everything except the 1,000-page file
//   dotnet run --project tools/Papery.TestFiles -c Release -- --verify-only  re-check existing files
//
// Options: --out <folder>  --pages <n>  --large-mb <n>

using System.Diagnostics;
using Papery.TestFiles;
using Papery.TestFiles.Generators;
using PdfSharp.Fonts;

GlobalFontSettings.UseWindowsFontsUnderWindows = true;

string outDir = FindRepoRoot() is { } repo ? Path.Combine(repo, "PaperyTestFiles") : Path.GetFullPath("PaperyTestFiles");
bool skipLarge = false, verifyOnly = false;
int pages = 1000, largeMb = 300;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out": outDir = Path.GetFullPath(args[++i]); break;
        case "--skip-large": skipLarge = true; break;
        case "--verify-only": verifyOnly = true; break;
        case "--pages": pages = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--large-mb": largeMb = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        default:
            Console.Error.WriteLine($"Unknown option '{args[i]}'. See the top of Program.cs for usage.");
            return 2;
    }
}

string tempDir = Path.Combine(outDir, "_temp");
var warnings = new List<string>();
var total = Stopwatch.StartNew();
Console.WriteLine($"Papery test files -> {outDir}");

if (!verifyOnly)
{
    Directory.CreateDirectory(outDir);
    byte[] photoJpeg = [];

    await Step("Images (JPG, PNG, BMP, TIFF, HEIC)", async () => photoJpeg = await Images.GenerateAsync(Path.Combine(outDir, "images"), warnings.Add));
    await Step("Receipt photo for OCR", () => Scans.ReceiptImageAsync(Path.Combine(outDir, "images", "receipt-scan.jpg"), tempDir));
    await Step("Scanned document (image only)", () => Scans.ScannedDocumentAsync(Path.Combine(outDir, "scanned-document.pdf"), tempDir));
    await Step("Interactive form", () => Run(() => Forms.InteractiveForm(Path.Combine(outDir, "forms-interactive.pdf"))));
    await Step("Flat printed form", () => Run(() => Forms.FlatForm(Path.Combine(outDir, "forms-flat.pdf"))));
    await Step("Password-protected and restricted", () => Run(() => Security.PasswordFiles(outDir)));
    await Step("Damaged files", () => Security.DamagedFilesAsync(outDir, photoJpeg));
    await Step("Bookmarks and links", () => Run(() => Documents.BookmarksAndLinks(Path.Combine(outDir, "bookmarks-and-links.pdf"))));
    await Step("Mixed page sizes and rotations", () => Run(() => Documents.MixedSizesAndRotations(Path.Combine(outDir, "mixed-sizes-and-rotations.pdf"))));
    await Step("Hidden data", () => Run(() => Privacy.HiddenData(Path.Combine(outDir, "hidden-data.pdf"))));
    await Step("Redaction test", () => Privacy.RedactionTestAsync(Path.Combine(outDir, "redaction-test.pdf"), tempDir));
    await Step("Merge set (20 files)", () => Run(() => Documents.MergeSet(Path.Combine(outDir, "merge-set"))));
    if (!skipLarge)
    {
        await Step($"Long document ({pages} pages, ~{largeMb} MB)", () =>
            Documents.LongDocumentAsync(Path.Combine(outDir, "long-1000-pages.pdf"), pages, largeMb, Console.WriteLine));
    }

    if (Directory.Exists(tempDir))
    {
        Directory.Delete(tempDir, recursive: true);
    }

    await File.WriteAllTextAsync(Path.Combine(outDir, "README.txt"), Readme.Text);
}

int failures = await Verifier.RunAsync(outDir, pages);

foreach (string warning in warnings)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"WARNING: {warning}");
    Console.ResetColor();
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"All files verified. Total time {total.Elapsed.TotalSeconds:0}s."
    : $"{failures} file(s) did not behave as expected.");
return failures == 0 ? 0 : 1;

static async Task Step(string name, Func<Task> work)
{
    Console.WriteLine($"- {name}...");
    var timer = Stopwatch.StartNew();
    await work();
    Console.WriteLine(Layout.Inv($"  done in {timer.Elapsed.TotalSeconds:0.0}s"));
}

static Task Run(Action work)
{
    work();
    return Task.CompletedTask;
}

static string? FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Papery.slnx")))
        {
            return dir.FullName;
        }
    }

    return null;
}

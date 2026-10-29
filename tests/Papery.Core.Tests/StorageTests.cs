using Papery.Core.Storage;
using Papery.Core.Viewing;

namespace Papery.Core.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("papery-core-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task View_state_survives_a_restart()
    {
        string file = Path.Combine(_folder, "state.json");
        var store = new ViewStateStore(file);
        store.Set(@"C:\Docs\Report.pdf", page: 41, zoom: 1.5, ViewMode.TwoPage);
        store.AddRecent(@"C:\Docs\Report.pdf");
        await store.SaveAsync();

        var reloaded = new ViewStateStore(file);
        var state = reloaded.Get(@"c:\docs\REPORT.pdf"); // Windows paths are case-insensitive

        Assert.NotNull(state);
        Assert.Equal(41, state.Page);
        Assert.Equal(1.5, state.Zoom);
        Assert.Equal(ViewMode.TwoPage, state.ViewMode);
        Assert.Equal([@"C:\Docs\Report.pdf"], reloaded.RecentFiles);
    }

    [Fact]
    public void Recent_files_are_most_recent_first_without_duplicates_and_capped()
    {
        var store = new ViewStateStore(Path.Combine(_folder, "state.json"));
        for (int i = 0; i < 30; i++)
        {
            store.AddRecent($@"C:\f{i}.pdf");
        }

        store.AddRecent(@"C:\F25.PDF");

        Assert.Equal(ViewStateStore.MaxRecentFiles, store.RecentFiles.Count);
        Assert.Equal(@"C:\F25.PDF", store.RecentFiles[0]);
        Assert.Single(store.RecentFiles, p => p.Equals(@"C:\f25.pdf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Corrupt_files_fall_back_to_defaults_instead_of_crashing()
    {
        string file = Path.Combine(_folder, "settings.json");
        File.WriteAllText(file, "{ this is not json");

        var settings = new SettingsStore(file);

        Assert.Equal(AppTheme.System, settings.Current.Theme);
    }

    [Fact]
    public async Task Settings_are_saved_and_reloaded()
    {
        string file = Path.Combine(_folder, "settings.json");
        var settings = new SettingsStore(file);
        int changes = 0;
        settings.Changed += (_, _) => changes++;

        settings.Update(s => s.ReadingMode = ReadingMode.Sepia);
        await Task.Delay(200); // save happens in the background

        Assert.Equal(1, changes);
        Assert.Equal(ReadingMode.Sepia, new SettingsStore(file).Current.ReadingMode);
    }
}

using Windows.UI.StartScreen;

namespace Papery.Services;

/// <summary>Recent files on the taskbar icon's right-click list.</summary>
internal static class JumpListService
{
    private const int MostRecent = 12;
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public static async Task AddRecentAsync(string path)
    {
        if (!JumpList.IsSupported())
        {
            return;
        }

        await OneAtATime.WaitAsync();
        try
        {
            var list = await JumpList.LoadCurrentAsync();
            list.SystemGroupKind = JumpListSystemGroupKind.None;
            string arguments = $"\"{path}\"";
            foreach (var old in list.Items.Where(i => i.Arguments == arguments).ToArray())
            {
                list.Items.Remove(old);
            }

            var item = JumpListItem.CreateWithArguments(arguments, Path.GetFileNameWithoutExtension(path));
            item.GroupName = "Recent";
            item.Description = path;
            list.Items.Insert(0, item);
            while (list.Items.Count > MostRecent)
            {
                list.Items.RemoveAt(list.Items.Count - 1);
            }

            await list.SaveAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A nicety: never worth troubling anyone about.
            Log.Info($"Jump list not updated: {ex.GetType().Name}");
        }
        finally
        {
            OneAtATime.Release();
        }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Papery.Services;

/// <summary>
/// Shows dialogs one at a time (WinUI allows only one open ContentDialog per window) with
/// consistent styling. All wording is calm and specific: what happened and what to do.
/// </summary>
internal static class Dialogs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, XamlRoot root)
    {
        dialog.XamlRoot = root;
        dialog.Style ??= (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        // Dialogs float above the window and would otherwise follow Windows' theme, not Papery's.
        if (root.Content is FrameworkElement content)
        {
            dialog.RequestedTheme = content.ActualTheme;
        }
        if (dialog.DefaultButton == ContentDialogButton.None && dialog.PrimaryButtonText is { Length: > 0 })
        {
            dialog.DefaultButton = ContentDialogButton.Primary;
        }

        await Gate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <param name="mood">Optionally show the Papery character, e.g. Oops for a file that can't be opened.</param>
    public static Task ShowMessageAsync(XamlRoot root, string title, string message, Controls.MascotMood? mood = null)
    {
        object content = Wrap(message);
        if (mood is { } m)
        {
            var panel = new StackPanel { Spacing = 14 };
            panel.Children.Add(new Controls.Mascot { Mood = m, Width = 56, Height = 66, IsFloating = false, HorizontalAlignment = HorizontalAlignment.Left });
            panel.Children.Add(Wrap(message));
            content = panel;
        }

        return ShowAsync(new ContentDialog { Title = title, Content = content, CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close }, root);
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string primaryText, string closeText = "Cancel") =>
        await ShowAsync(new ContentDialog { Title = title, Content = Wrap(message), PrimaryButtonText = primaryText, CloseButtonText = closeText }, root)
            == ContentDialogResult.Primary;

    /// <summary>Asks for a PDF's password. Returns null if the user cancels.</summary>
    public static async Task<string?> AskPasswordAsync(XamlRoot root, string fileName, bool previousWasWrong)
    {
        var box = new PasswordBox { PlaceholderText = "Password", Width = 320 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Wrap(previousWasWrong
            ? "That password didn't work. Check Caps Lock and try again."
            : $"\"{fileName}\" is protected with a password."));
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            Title = previousWasWrong ? "Try the password again" : "Enter the password",
            Content = panel,
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            IsPrimaryButtonEnabled = false,
        };
        box.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Password.Length > 0;
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);

        // Enter in the box submits straight away (the default-button route can miss the first press).
        bool submitted = false;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && box.Password.Length > 0)
            {
                submitted = true;
                e.Handled = true;
                dialog.Hide();
            }
        };
        var result = await ShowAsync(dialog, root);
        return submitted || result == ContentDialogResult.Primary ? box.Password : null;
    }

    public static TextBlock Wrap(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 };
}

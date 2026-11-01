using Microsoft.UI.Xaml;
using Papery.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Papery.Views;

/// <summary>Reads pages aloud with Windows' own voices, which work offline. Starts at the page on screen and turns pages as it goes.</summary>
public sealed partial class DocumentView
{
    private CancellationTokenSource? _reading;

    private void OnReadAloud(object sender, RoutedEventArgs e) => Guard.Run(ToggleReadAloudAsync, "Reading aloud");

    private async Task ToggleReadAloudAsync()
    {
        if (ReadAloudButton.IsChecked != true)
        {
            StopReadingAloud();
            return;
        }

        await ReadAloudAsync();
    }

    private void StopReadingAloud()
    {
        _reading?.Cancel();
        _reading = null;
        ReadAloudButton.IsChecked = false;
    }

    private async Task ReadAloudAsync()
    {
        if (_document is not { } document)
        {
            ReadAloudButton.IsChecked = false;
            return;
        }

        _reading?.Cancel();
        var cancel = _reading = new CancellationTokenSource();
        using var player = new MediaPlayer();
        bool readSomething = false;
        try
        {
            using var voice = new SpeechSynthesizer();
            for (int page = Math.Max(0, Viewer.CurrentPage); page < document.PageCount && !cancel.IsCancellationRequested; page++)
            {
                string text = await document.GetPageTextAsync(page, cancellationToken: cancel.Token);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (readSomething)
                {
                    Viewer.GoToPage(page);
                }

                readSomething = true;
                using var speech = await voice.SynthesizeTextToStreamAsync(text);
                player.Source = MediaSource.CreateFromStream(speech, speech.ContentType);
                await PlayToEndAsync(player, cancel.Token);
            }

            if (!readSomething && !cancel.IsCancellationRequested)
            {
                Toaster.Show("No words to read from here. A scan needs Make searchable first", "");
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose.
        }
        catch (ObjectDisposedException)
        {
            // The tab closed or the file changed while it was reading.
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Info($"Read aloud stopped: {ex.GetType().Name}");
            Toaster.Show("Windows couldn't read this aloud. Check a voice is installed in Settings > Time & language > Speech", "", seconds: 7);
        }
        finally
        {
            player.Pause();
            if (_reading == cancel)
            {
                _reading = null;
                ReadAloudButton.IsChecked = false;
            }

            cancel.Dispose();
        }
    }

    private static async Task PlayToEndAsync(MediaPlayer player, CancellationToken cancel)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Ended(MediaPlayer sender, object args) => finished.TrySetResult();
        void Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => finished.TrySetException(new InvalidOperationException(args.ErrorMessage));
        player.MediaEnded += Ended;
        player.MediaFailed += Failed;
        try
        {
            await using (cancel.Register(() => finished.TrySetCanceled(cancel)))
            {
                player.Play();
                await finished.Task;
            }
        }
        finally
        {
            player.MediaEnded -= Ended;
            player.MediaFailed -= Failed;
        }
    }
}

using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Papery.Controls;
using Papery.Pdf;
using Papery.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Papery.Views;

/// <summary>What the signature maker produced.</summary>
public sealed record NewSignature(PdfBitmap Picture, bool IsInitials);

/// <summary>Make a signature or initials: draw it, type it in a handwriting font, or import a picture.</summary>
internal static class SignatureDialog
{
    private const int MaxImportSide = 1200;

    private static readonly string[] ImageTypes = [".png", ".jpg", ".jpeg", ".bmp", ".heic", ".webp", ".tif", ".tiff"];

    private static readonly string[] Handwriting = ["Segoe Script", "Ink Free", "Lucida Handwriting", "Bradley Hand ITC", "Segoe Print"];

    public static async Task<NewSignature?> ShowAsync(XamlRoot root, nint hwnd, bool initials)
    {
        var modes = new PillBar();
        modes.AddItem("Draw", "", "draw");
        modes.AddItem("Type", "", "type");
        modes.AddItem("Import", "", "import");

        // ---- draw ----
        var ink = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x10, 0x20, 0x60));
        var drawing = new Canvas { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        var strokes = new List<Polyline>();
        Polyline? current = null;
        drawing.PointerPressed += (_, e) =>
        {
            drawing.CapturePointer(e.Pointer);
            current = new Polyline { Stroke = ink, StrokeThickness = 2.4, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            current.Points.Add(e.GetCurrentPoint(drawing).Position);
            drawing.Children.Add(current);
            strokes.Add(current);
            e.Handled = true;
        };
        drawing.PointerMoved += (_, e) =>
        {
            if (current != null)
            {
                current.Points.Add(e.GetCurrentPoint(drawing).Position);
                e.Handled = true;
            }
        };
        void EndStroke(object sender, PointerRoutedEventArgs e)
        {
            current = null;
            drawing.ReleasePointerCaptures();
        }

        drawing.PointerReleased += EndStroke;
        drawing.PointerCanceled += EndStroke;
        var clear = new Button { Content = "Clear", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Style = (Style)Application.Current.Resources["PaperyQuietButtonStyle"] };
        clear.Click += (_, _) =>
        {
            drawing.Children.Clear();
            strokes.Clear();
        };
        var line = new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(24, 0, 24, 44), Fill = new SolidColorBrush(Microsoft.UI.Colors.LightGray), IsHitTestVisible = false };

        // ---- type ----
        var words = new TextBox { PlaceholderText = initials ? "Your initials" : "Your name" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(words, words.PlaceholderText);
        var typed = new TextBlock { FontSize = 44, Foreground = ink, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8) };
        var fonts = new ComboBox { MinWidth = 220 };
        foreach (string font in Handwriting)
        {
            fonts.Items.Add(new ComboBoxItem { Content = font, FontFamily = new FontFamily(font) });
        }

        fonts.SelectedIndex = 0;
        fonts.SelectionChanged += (_, _) => typed.FontFamily = new FontFamily(Handwriting[fonts.SelectedIndex]);
        typed.FontFamily = new FontFamily(Handwriting[0]);
        words.TextChanged += (_, _) => typed.Text = words.Text;
        var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        words.Width = 220;
        typeRow.Children.Add(words);
        typeRow.Children.Add(fonts);

        // ---- import ----
        PdfBitmap? imported = null;
        var importedImage = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(8) };
        var choose = new Button { Content = "Choose a picture", Style = (Style)Application.Current.Resources["PaperySecondaryButtonStyle"] };
        var importHint = new TextBlock
        {
            Text = "Sign on white paper and take a photo. Papery removes the white background.",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 300,
            Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"],
        };
        var importRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        importRow.Children.Add(choose);
        importRow.Children.Add(importHint);

        // ---- the white "paper" everything is made on ----
        var paper = new Grid { Height = 170, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Microsoft.UI.Colors.White), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.LightGray) };
        paper.Children.Add(line);
        paper.Children.Add(drawing);
        paper.Children.Add(typed);
        paper.Children.Add(importedImage);
        paper.Children.Add(clear);

        var note = new TextBlock
        {
            Text = "This places a picture of your signature on the page. It's an ordinary e-signature, not a certified digital signature.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"],
        };
        var panel = new StackPanel { Spacing = 12, Width = 480 };
        panel.Children.Add(modes);
        panel.Children.Add(typeRow);
        panel.Children.Add(importRow);
        panel.Children.Add(paper);
        panel.Children.Add(note);

        void ShowMode()
        {
            string mode = (string)modes.Items[Math.Max(0, modes.SelectedIndex)].Tag;
            drawing.Visibility = line.Visibility = clear.Visibility = mode == "draw" ? Visibility.Visible : Visibility.Collapsed;
            typed.Visibility = typeRow.Visibility = mode == "type" ? Visibility.Visible : Visibility.Collapsed;
            importedImage.Visibility = importRow.Visibility = mode == "import" ? Visibility.Visible : Visibility.Collapsed;
        }

        modes.SelectionChanged += (_, _) => ShowMode();
        modes.SelectedIndex = 0;
        ShowMode();

        choose.Click += (_, _) => Guard.Run(async () =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
            foreach (string type in ImageTypes)
            {
                picker.FileTypeFilter.Add(type);
            }

            InitializeWithWindow.Initialize(picker, hwnd);
            if (await picker.PickSingleFileAsync() is not { } file)
            {
                return;
            }

            imported = await LoadWithoutBackgroundAsync(file);
            importedImage.Source = PageBitmaps.ToWriteableBitmap(imported);
        }, "Importing a signature");

        var dialog = new ContentDialog
        {
            Title = initials ? "Your initials" : "Your signature",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
        };

        NewSignature? result = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                string mode = (string)modes.Items[Math.Max(0, modes.SelectedIndex)].Tag;
                PdfBitmap? picture = mode switch
                {
                    "import" => imported,
                    "draw" when strokes.Count > 0 => await CaptureAsync(drawing),
                    "type" when !string.IsNullOrWhiteSpace(words.Text) => await CaptureAsync(typed),
                    _ => null,
                };
                if (picture == null)
                {
                    args.Cancel = true; // nothing made yet: stay open
                    return;
                }

                result = new NewSignature(picture, initials);
            }
            finally
            {
                deferral.Complete();
            }
        };

        await Dialogs.ShowAsync(dialog, root);
        return result;
    }

    /// <summary>A control's look as pixels, trimmed to what's drawn. Empty areas stay transparent.</summary>
    private static async Task<PdfBitmap?> CaptureAsync(FrameworkElement element)
    {
        var target = new RenderTargetBitmap();
        await target.RenderAsync(element);
        var pixels = (await target.GetPixelsAsync()).ToArray();
        int w = target.PixelWidth, h = target.PixelHeight;

        // The capture is premultiplied; PDFs want plain colour plus alpha.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a is > 0 and < 255)
            {
                pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
            }
        }

        return Trim(pixels, w, h);
    }

    /// <summary>Loads a picture and turns its light background transparent, keeping soft pen edges.</summary>
    private static async Task<PdfBitmap> LoadWithoutBackgroundAsync(StorageFile file)
    {
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        double scale = Math.Min(1, (double)MaxImportSide / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform { ScaledWidth = (uint)(decoder.PixelWidth * scale), ScaledHeight = (uint)(decoder.PixelHeight * scale) };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        byte[] pixels = data.DetachPixelData();
        return await Task.Run(() => RemoveBackground(pixels, (int)transform.ScaledWidth, (int)transform.ScaledHeight));
    }

    private static PdfBitmap RemoveBackground(byte[] pixels, int width, int height)
    {
        // Paper is the bright part of the photo, whatever its exact shade under the lamp.
        var histogram = new int[256];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            histogram[((pixels[i + 2] * 299) + (pixels[i + 1] * 587) + (pixels[i] * 114)) / 1000]++;
        }

        int paper = 128;
        for (int v = 129; v < 256; v++)
        {
            if (histogram[v] > histogram[paper])
            {
                paper = v;
            }
        }

        // Fully see-through near paper colour, fully solid well below it, a soft ramp between.
        double solid = paper * 0.55, clear = paper * 0.9;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int luma = ((pixels[i + 2] * 299) + (pixels[i + 1] * 587) + (pixels[i] * 114)) / 1000;
            double alpha = Math.Clamp((clear - luma) / Math.Max(1, clear - solid), 0, 1);
            pixels[i + 3] = (byte)(pixels[i + 3] * alpha);
        }

        return Trim(pixels, width, height) ?? new PdfBitmap(width, height, width * 4, pixels);
    }

    /// <summary>Cuts a picture down to its visible part, so it can be placed by what you can see.</summary>
    private static PdfBitmap? Trim(byte[] pixels, int w, int h)
    {
        int left = w, right = -1, top = h, bottom = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (pixels[(((y * w) + x) * 4) + 3] > 16)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        if (right < 0)
        {
            return null;
        }

        int outW = right - left + 1, outH = bottom - top + 1;
        var cut = new byte[outW * outH * 4];
        for (int y = 0; y < outH; y++)
        {
            Buffer.BlockCopy(pixels, (((y + top) * w) + left) * 4, cut, y * outW * 4, outW * 4);
        }

        return new PdfBitmap(outW, outH, outW * 4, cut);
    }
}

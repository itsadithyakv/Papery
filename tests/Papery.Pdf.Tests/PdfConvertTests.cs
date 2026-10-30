using Papery.Pdf.Editing;

namespace Papery.Pdf.Tests;

public sealed class PdfConvertTests : IDisposable
{
    // The smallest valid colour JPEG: 1x1, mid grey.
    private const string TinyJpeg =
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFAEBAAAAAAAAAAAAAAAAAAAAAP/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/AL+AAf/Z";

    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    private static PdfBitmap Solid(int width, int height, byte blue, byte green, byte red)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blue;
            pixels[i + 1] = green;
            pixels[i + 2] = red;
            pixels[i + 3] = 255;
        }

        return new PdfBitmap(width, height, width * 4, pixels);
    }

    private static PdfImagePage Page(PdfBitmap bitmap, double width, double height) =>
        new(width, height, () => Task.FromResult(new PdfImageData(null, bitmap)));

    private string Output => Path.Combine(_files.Folder, $"out-{Guid.NewGuid():N}.pdf");

    // ================================================================= layout

    [Fact]
    public void Matching_the_picture_makes_the_page_its_size_plus_margins()
    {
        var place = ImagePageLayout.Place(300, 200, new ImagePageOptions(Margin: 10));

        Assert.Equal((320.0, 220.0), (place.PageWidth, place.PageHeight));
        Assert.Equal((10.0, 10.0), (place.X, place.Y));
        Assert.Equal((300.0, 200.0), (place.Width, place.Height));
    }

    [Fact]
    public void Auto_orientation_turns_the_paper_to_suit()
    {
        var wide = ImagePageLayout.Place(400, 200, new ImagePageOptions(ImagePaperSize.A4));
        var tall = ImagePageLayout.Place(200, 400, new ImagePageOptions(ImagePaperSize.A4));

        Assert.True(wide.PageWidth > wide.PageHeight);
        Assert.True(tall.PageHeight > tall.PageWidth);
    }

    [Fact]
    public void A_set_orientation_is_kept_and_the_picture_stays_whole_and_centred()
    {
        var place = ImagePageLayout.Place(400, 200, new ImagePageOptions(ImagePaperSize.Letter, ImageOrientation.Portrait, 36));

        Assert.Equal((612.0, 792.0), (place.PageWidth, place.PageHeight));
        Assert.Equal(540, place.Width, 3); // the width is what limits it
        Assert.Equal(270, place.Height, 3);
        Assert.Equal(36, place.X, 3);
        Assert.Equal((792 - 270) / 2.0, place.Y, 3);
    }

    [Fact]
    public void Silly_margins_are_ignored_rather_than_failing()
    {
        var place = ImagePageLayout.Place(100, 100, new ImagePageOptions(ImagePaperSize.A4, Margin: 5000));

        Assert.True(place.Width > 0 && place.Height > 0);
    }

    // ================================================================= making PDFs

    [Fact]
    public async Task Each_picture_becomes_a_page_in_order()
    {
        string path = Output;

        await Engine.ImagesToPdfAsync(
            [Page(Solid(20, 20, 0, 0, 255), 100, 100), Page(Solid(20, 40, 255, 0, 0), 100, 200)],
            new ImagePageOptions(),
            path);

        await using var doc = await Engine.OpenAsync(path);
        Assert.Equal(2, doc.PageCount);
        Assert.Equal(100, doc.PageSizes[0].Height, 1);
        Assert.Equal(200, doc.PageSizes[1].Height, 1);

        var first = await doc.RenderPageAsync(0, 10, 10);
        Assert.Equal((0, 0, 255), (first.Pixels[(5 * first.Stride) + 20], first.Pixels[(5 * first.Stride) + 21], first.Pixels[(5 * first.Stride) + 22]));
        var second = await doc.RenderPageAsync(1, 10, 20);
        Assert.Equal((255, 0, 0), (second.Pixels[(10 * second.Stride) + 20], second.Pixels[(10 * second.Stride) + 21], second.Pixels[(10 * second.Stride) + 22]));
    }

    [Fact]
    public async Task A_jpeg_is_kept_as_it_is()
    {
        string path = Output;
        byte[] jpeg = Convert.FromBase64String(TinyJpeg);

        await Engine.ImagesToPdfAsync([new PdfImagePage(50, 50, () => Task.FromResult(new PdfImageData(jpeg, null)))], new ImagePageOptions(), path);

        await using var doc = await Engine.OpenAsync(path);
        Assert.Equal(1, doc.PageCount);
        var images = await doc.GetPageImagesAsync(0);
        Assert.Empty(images); // a single pixel is below the size worth extracting, but the page is valid

        // The JPEG's own bytes are in the file: nothing was decoded and re-encoded.
        byte[] saved = await File.ReadAllBytesAsync(path);
        Assert.True(saved.AsSpan().IndexOf(jpeg) >= 0);
    }

    [Fact]
    public async Task Nothing_to_add_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Engine.ImagesToPdfAsync([], new ImagePageOptions(), Output));
    }

    [Fact]
    public async Task A_failure_leaves_nothing_behind()
    {
        string path = Output;
        var broken = new PdfImagePage(50, 50, () => Task.FromResult(new PdfImageData(null, null)));

        await Assert.ThrowsAsync<PdfEditException>(() => Engine.ImagesToPdfAsync([broken], new ImagePageOptions(), path));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_files.Folder, "*.pdf"));
    }

    // ================================================================= getting pictures back out

    [Fact]
    public async Task Pictures_come_back_out_with_their_colours()
    {
        string path = Output;
        await Engine.ImagesToPdfAsync([Page(Solid(30, 40, 10, 20, 200), 100, 100)], new ImagePageOptions(), path);

        await using var doc = await Engine.OpenAsync(path);
        var picture = Assert.Single(await doc.GetPageImagesAsync(0));

        Assert.Equal((30, 40), (picture.Width, picture.Height)); // its own pixels, not the size it's shown at
        int middle = ((picture.Height / 2) * picture.Stride) + ((picture.Width / 2) * 4);
        Assert.InRange(picture.Pixels[middle], (byte)5, (byte)15);
        Assert.InRange(picture.Pixels[middle + 2], (byte)195, (byte)205);
    }

    [Fact]
    public async Task A_page_with_only_words_has_no_pictures()
    {
        await using var doc = await Engine.OpenAsync(_files.Create(1));

        Assert.Empty(await doc.GetPageImagesAsync(0));
    }
}

using System.Text.Json;
using Papery.Pdf;
using Papery.Services.Licensing;

namespace Papery.Services;

/// <summary>A saved signature or set of initials: straight-alpha BGRA pixels.</summary>
public sealed record SavedSignature(string Id, bool IsInitials, PdfBitmap Picture);

/// <summary>
/// Signatures kept on this PC for reuse. They're encrypted for the signed-in Windows user, because a
/// signature is personal; nothing ever leaves the computer.
/// </summary>
internal sealed class SignatureStore
{
    private readonly string _folder = Path.Combine(AppServices.DataFolder, "signatures");
    private readonly DpapiProtector _protector = new();

    public IReadOnlyList<SavedSignature> Load()
    {
        if (!Directory.Exists(_folder))
        {
            return [];
        }

        var result = new List<SavedSignature>();
        foreach (string path in Directory.GetFiles(_folder, "*.sig").OrderBy(File.GetCreationTimeUtc))
        {
            try
            {
                using var reader = new BinaryReader(new MemoryStream(_protector.Unprotect(File.ReadAllBytes(path))));
                bool initials = reader.ReadBoolean();
                int width = reader.ReadInt32(), height = reader.ReadInt32();
                byte[] pixels = reader.ReadBytes(width * height * 4);
                result.Add(new SavedSignature(Path.GetFileNameWithoutExtension(path), initials, new PdfBitmap(width, height, width * 4, pixels)));
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
            {
                Log.Error("A saved signature couldn't be read", ex);
            }
        }

        return result;
    }

    public SavedSignature Save(PdfBitmap picture, bool isInitials)
    {
        Directory.CreateDirectory(_folder);
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(isInitials);
            writer.Write(picture.Width);
            writer.Write(picture.Height);
            writer.Write(picture.Pixels);
        }

        string id = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(Path.Combine(_folder, id + ".sig"), _protector.Protect(data.ToArray()));
        return new SavedSignature(id, isInitials, picture);
    }

    public void Delete(string id)
    {
        try
        {
            File.Delete(Path.Combine(_folder, id + ".sig"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("A saved signature couldn't be deleted", ex);
        }
    }
}

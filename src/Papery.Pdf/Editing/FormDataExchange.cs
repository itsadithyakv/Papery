using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Papery.Pdf.Editing;

public enum FormDataFormat
{
    /// <summary>Two columns, field name and answer, in UTF-8 with a BOM so Excel reads it right.</summary>
    Csv,

    /// <summary>Adobe's XML for form data, which Acrobat imports and exports.</summary>
    Xfdf,
}

/// <summary>What reading form answers in did.</summary>
/// <param name="Filled">Fields that now hold the answer from the file.</param>
/// <param name="Unknown">Names in the file this form has no field for.</param>
/// <param name="Skipped">Fields that couldn't take the answer: read-only ones, signatures, or a choice the list doesn't offer.</param>
public sealed record FormImportResult(int Filled, int Unknown, int Skipped);

/// <summary>
/// Form answers out to a CSV or XFDF file and back in by field name, so one set of answers can fill many copies of
/// a form, or move between Papery and Acrobat. Filling goes through PDFium's form engine like typing does, so the
/// fields' look is redrawn properly.
/// </summary>
internal static class FormDataExchange
{
    /// <summary>More than any real form has; past this the rest of a file is ignored.</summary>
    public const int MostFields = 10_000;

    private const int DeepestName = 32;
    private const string CsvHeaderName = "Field";
    private const string CsvHeaderValue = "Value";

    // ================================================================= out

    public static async Task<int> ExportAsync(PdfDocument document, string path, FormDataFormat format, CancellationToken cancellationToken)
    {
        var answers = await ReadAnswersAsync(document, cancellationToken).ConfigureAwait(false);
        if (answers.Count == 0)
        {
            return 0;
        }

        string pdfName = Path.GetFileName(document.FilePath);
        await Task.Run(() => Xfdf.WriteSafely(path, stream =>
        {
            if (format == FormDataFormat.Csv)
            {
                WriteCsv(stream, answers);
            }
            else
            {
                WriteXfdf(stream, answers, pdfName);
            }
        }), cancellationToken).ConfigureAwait(false);
        return answers.Count;
    }

    /// <summary>Each field's answer once, in form order. Ticked boxes give their "on" name, unticked ones "Off", as Acrobat does.</summary>
    internal static async Task<List<(string Name, string Value)>> ReadAnswersAsync(PdfDocument document, CancellationToken cancellationToken)
    {
        var answers = new List<(string Name, string Value)>();
        if (document.FormType == PdfFormType.None)
        {
            return answers;
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int page = 0; page < document.PageCount; page++)
        {
            foreach (var field in await document.GetFormFieldsAsync(page, cancellationToken).ConfigureAwait(false))
            {
                if (field.Kind is PdfFormFieldKind.Signature or PdfFormFieldKind.Button || field.Name.Length == 0)
                {
                    continue;
                }

                string? value = field.Kind switch
                {
                    PdfFormFieldKind.CheckBox or PdfFormFieldKind.RadioButton => field.IsChecked ? await OnName(document, field, cancellationToken).ConfigureAwait(false) : null,
                    PdfFormFieldKind.ListBox when field.Value.Length == 0 && field.SelectedOption >= 0 => field.Options[field.SelectedOption],
                    _ => field.Value,
                };
                if (!seen.TryGetValue(field.Name, out int at))
                {
                    seen[field.Name] = answers.Count;
                    answers.Add((field.Name, value ?? "Off"));
                }
                else if (value != null)
                {
                    answers[at] = (field.Name, value); // a later button in the group is the ticked one
                }
            }
        }

        return answers;
    }

    private static async Task<string> OnName(PdfDocument document, PdfFormField field, CancellationToken cancellationToken)
    {
        string on = await document.GetFieldExportValueAsync(field.PageIndex, field.Index, cancellationToken).ConfigureAwait(false);
        return on.Length > 0 ? on : "Yes";
    }

    internal static void WriteCsv(Stream stream, IReadOnlyList<(string Name, string Value)> answers)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16, leaveOpen: true);
        writer.Write($"{CsvHeaderName},{CsvHeaderValue}\r\n");
        foreach (var (name, value) in answers)
        {
            writer.Write(CsvCell(name));
            writer.Write(',');
            writer.Write(CsvCell(value));
            writer.Write("\r\n");
        }
    }

    /// <summary>
    /// Always quoted, so commas and line breaks in answers are safe. Answers starting like a formula get an apostrophe,
    /// which Excel hides, so opening the file can never run anything; reading it back takes the apostrophe off.
    /// </summary>
    private static string CsvCell(string text)
    {
        if (text.Length > 0 && IsFormulaStart(text[0]))
        {
            text = "'" + text;
        }

        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static bool IsFormulaStart(char c) => c is '=' or '+' or '-' or '@' or '\t' or '\r';

    internal static void WriteXfdf(Stream stream, IReadOnlyList<(string Name, string Value)> answers, string pdfName)
    {
        // Acrobat nests dotted names ("address.city" is "city" inside "address"), so the file does the same.
        var root = new NameNode(string.Empty);
        foreach (var (name, value) in answers)
        {
            string[] parts = name.Split('.');
            if (parts.Any(p => p.Length == 0))
            {
                parts = [name]; // not a proper dotted name; keep it whole
            }

            var node = root;
            foreach (string part in parts)
            {
                node = node.Child(part);
            }

            node.Value = value;
        }

        using var xml = XmlWriter.Create(stream, Xfdf.WriterSettings);
        xml.WriteStartDocument();
        xml.WriteStartElement("xfdf", Xfdf.Namespace);
        xml.WriteAttributeString("xml", "space", null, "preserve");
        xml.WriteStartElement("fields", Xfdf.Namespace);
        foreach (var node in root.Kids)
        {
            node.Write(xml);
        }

        xml.WriteEndElement();
        xml.WriteStartElement("f", Xfdf.Namespace);
        xml.WriteAttributeString("href", Xfdf.Clean(pdfName));
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private sealed class NameNode(string part)
    {
        private readonly Dictionary<string, NameNode> _byPart = new(StringComparer.Ordinal);

        public List<NameNode> Kids { get; } = [];

        public string? Value { get; set; }

        public NameNode Child(string name)
        {
            if (!_byPart.TryGetValue(name, out var kid))
            {
                kid = new NameNode(name);
                _byPart[name] = kid;
                Kids.Add(kid);
            }

            return kid;
        }

        public void Write(XmlWriter xml)
        {
            xml.WriteStartElement("field", Xfdf.Namespace);
            xml.WriteAttributeString("name", Xfdf.Clean(part));
            if (Value != null)
            {
                xml.WriteElementString("value", Xfdf.Namespace, Xfdf.Clean(Value));
            }

            foreach (var kid in Kids)
            {
                kid.Write(xml);
            }

            xml.WriteEndElement();
        }
    }

    // ================================================================= in

    public static async Task<FormImportResult> ImportAsync(PdfEngine engine, PdfInputFile input, string path, string outputPath, CancellationToken cancellationToken)
    {
        var answers = await Task.Run(() => Read(path), cancellationToken).ConfigureAwait(false);
        await using var document = await engine.OpenAsync(input.Path, input.Password, cancellationToken).ConfigureAwait(false);
        if (!document.CanFillForms)
        {
            throw new PdfEditException(PdfEditError.NotAllowed, input.Path);
        }

        var byName = new Dictionary<string, List<PdfFormField>>(StringComparer.Ordinal);
        if (document.FormType != PdfFormType.None)
        {
            for (int page = 0; page < document.PageCount; page++)
            {
                foreach (var field in await document.GetFormFieldsAsync(page, cancellationToken).ConfigureAwait(false))
                {
                    if (!byName.TryGetValue(field.Name, out var widgets))
                    {
                        byName[field.Name] = widgets = [];
                    }

                    widgets.Add(field);
                }
            }
        }

        int filled = 0, unknown = 0, skipped = 0;
        foreach (var (name, values) in answers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byName.TryGetValue(name, out var widgets))
            {
                unknown++;
            }
            else if (await FillAsync(document, widgets, values, cancellationToken).ConfigureAwait(false))
            {
                filled++;
            }
            else
            {
                skipped++;
            }
        }

        if (filled > 0)
        {
            await document.SaveAsync(outputPath, cancellationToken).ConfigureAwait(false);
        }

        return new FormImportResult(filled, unknown, skipped);
    }

    private static async Task<bool> FillAsync(PdfDocument document, List<PdfFormField> widgets, IReadOnlyList<string> values, CancellationToken cancellationToken)
    {
        var first = widgets[0];
        string value = values.Count > 0 ? values[0] : string.Empty;
        if (first.IsReadOnly || first.Kind is PdfFormFieldKind.Signature or PdfFormFieldKind.Button)
        {
            return false;
        }

        switch (first.Kind)
        {
            case PdfFormFieldKind.CheckBox or PdfFormFieldKind.RadioButton:
            {
                if (IsOff(value))
                {
                    foreach (var ticked in widgets.Where(w => w.IsChecked))
                    {
                        await document.SetFieldCheckedAsync(ticked.PageIndex, ticked.Index, false, cancellationToken).ConfigureAwait(false);
                    }

                    return true;
                }

                foreach (var widget in widgets)
                {
                    if (string.Equals(await document.GetFieldExportValueAsync(widget.PageIndex, widget.Index, cancellationToken).ConfigureAwait(false), value, StringComparison.OrdinalIgnoreCase))
                    {
                        await document.SetFieldCheckedAsync(widget.PageIndex, widget.Index, true, cancellationToken).ConfigureAwait(false);
                        return true;
                    }
                }

                // A spreadsheet may say "yes" where the box's own name is something else.
                if (first.Kind == PdfFormFieldKind.CheckBox && widgets.Count == 1 && IsOn(value))
                {
                    await document.SetFieldCheckedAsync(first.PageIndex, first.Index, true, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                return false;
            }

            case PdfFormFieldKind.ComboBox or PdfFormFieldKind.ListBox:
            {
                var options = values.Select(v => Option(first.Options, v)).ToList();
                if (options.Count > 0 && options.All(o => o >= 0))
                {
                    foreach (int option in first.Kind == PdfFormFieldKind.ComboBox ? options.Take(1) : options)
                    {
                        await document.SetFieldOptionAsync(first.PageIndex, first.Index, option, cancellationToken).ConfigureAwait(false);
                    }

                    return true;
                }

                if (first.Kind == PdfFormFieldKind.ListBox)
                {
                    return false;
                }

                // Not one of the choices: only a dropdown that allows typing takes it, so check it did.
                await document.SetFieldTextAsync(first.PageIndex, first.Index, value, cancellationToken).ConfigureAwait(false);
                var now = (await document.GetFormFieldsAsync(first.PageIndex, cancellationToken).ConfigureAwait(false)).FirstOrDefault(f => f.Index == first.Index);
                return now?.Value == value;
            }

            default:
                if (first.Value != value)
                {
                    await document.SetFieldTextAsync(first.PageIndex, first.Index, value, cancellationToken).ConfigureAwait(false);
                }

                return true;
        }
    }

    private static int Option(IReadOnlyList<string> options, string value)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            var comparison = pass == 0 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            for (int i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i], value, comparison))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool IsOff(string value) =>
        value.Trim() is "" || value.Trim().ToUpperInvariant() is "OFF" or "NO" or "FALSE" or "0" or "UNCHECKED";

    private static bool IsOn(string value) =>
        value.Trim().ToUpperInvariant() is "YES" or "ON" or "TRUE" or "1" or "X" or "CHECKED";

    /// <summary>The answers in a CSV or XFDF file (told apart by their first character). Throws InvalidDataException for anything else.</summary>
    internal static List<(string Name, IReadOnlyList<string> Values)> Read(string path)
    {
        byte[] bytes;
        using (var stream = Xfdf.OpenChecked(path))
        {
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        string text = Decode(bytes);
        return text.TrimStart().StartsWith('<') ? ReadXfdf(bytes) : ReadCsv(text);
    }

    /// <summary>UTF-8 or UTF-16 by their BOM; without one, UTF-8 if it is valid, otherwise the Western code page older Excel saves in.</summary>
    private static string Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes is [0xFF, 0xFE, ..])
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes is [0xFE, 0xFF, ..])
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static List<(string Name, IReadOnlyList<string> Values)> ReadXfdf(byte[] bytes)
    {
        var answers = new List<(string Name, IReadOnlyList<string> Values)>();
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes), Xfdf.ReaderSettings);
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "xfdf")
            {
                throw new InvalidDataException("That file isn't XFDF form data.");
            }

            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1)
                {
                    if (reader.LocalName == "fields")
                    {
                        Collect((XElement)XNode.ReadFrom(reader), string.Empty, 0, answers);
                    }
                    else
                    {
                        reader.Skip(); // comments, ids and the like
                    }

                    continue;
                }

                reader.Read();
            }
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("That file isn't XFDF form data Papery can read.", ex);
        }

        return answers;
    }

    private static void Collect(XElement parent, string prefix, int depth, List<(string Name, IReadOnlyList<string> Values)> answers)
    {
        if (depth > DeepestName)
        {
            return;
        }

        foreach (var field in parent.Elements().Where(e => e.Name.LocalName == "field"))
        {
            if (answers.Count >= MostFields)
            {
                return;
            }

            if (field.Attribute("name")?.Value is not { Length: > 0 } part)
            {
                continue;
            }

            string name = prefix.Length == 0 ? part : $"{prefix}.{part}";
            var values = field.Elements()
                .Where(e => e.Name.LocalName is "value" or "value-richtext")
                .Take(1000)
                .Select(e => Xfdf.Clean(e.Value))
                .ToArray();
            if (values.Length > 0)
            {
                answers.Add((name, values));
            }

            Collect(field, name, depth + 1, answers);
        }
    }

    private static List<(string Name, IReadOnlyList<string> Values)> ReadCsv(string text)
    {
        var answers = new List<(string Name, IReadOnlyList<string> Values)>();
        char separator = Separator(text);
        bool first = true;
        foreach (var row in CsvRows(text, separator))
        {
            if (answers.Count >= MostFields)
            {
                break;
            }

            bool header = first && row.Count >= 2 && row[0].Trim() is CsvHeaderName or "Field name" or "Name" && row[1].Trim() == CsvHeaderValue;
            first = false;
            if (header || row.Count == 0 || row[0].Length == 0)
            {
                continue;
            }

            answers.Add((row[0], [Unguarded(row.Count > 1 ? row[1] : string.Empty)]));
        }

        return answers;
    }

    /// <summary>Excel in much of Europe separates with semicolons; the first line says which this is.</summary>
    private static char Separator(string text)
    {
        int commas = 0, semicolons = 0;
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '\r' or '\n')
            {
                break;
            }
            else if (!quoted)
            {
                commas += c == ',' ? 1 : 0;
                semicolons += c == ';' ? 1 : 0;
            }
        }

        return semicolons > 0 && commas == 0 ? ';' : ',';
    }

    /// <summary>Rows of a CSV file as RFC 4180 has them: quoted cells may hold separators, quotes ("") and line breaks.</summary>
    private static IEnumerable<List<string>> CsvRows(string text, char separator)
    {
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else if (cell.Length < Xfdf.LongestText)
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                yield return row;
                row = [];
            }
            else if (cell.Length < Xfdf.LongestText)
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            yield return row;
        }
    }

    /// <summary>Takes off the apostrophe that guards a formula-like answer (see <see cref="CsvCell"/>).</summary>
    private static string Unguarded(string value) =>
        value.Length >= 2 && value[0] == '\'' && IsFormulaStart(value[1]) ? value[1..] : value;
}

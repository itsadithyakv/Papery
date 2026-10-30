using System.Runtime.InteropServices;
using Papery.Pdf.Editing;
using Papery.Pdf.Native;

namespace Papery.Pdf;

/// <summary>
/// Form fields. The screen controls are Papery's own; PDFium's form engine writes the answers, so
/// each field's value and appearance end up exactly as a PDF needs them to, however the field is set up.
/// Like annotations, answers go into this open document and reach the file on <see cref="SaveAsync"/>.
/// </summary>
public sealed partial class PdfDocument
{
    private nint _form;
    private unsafe void* _formInfo;

    /// <summary>The owner allows filling in the form (permission bits 6 or 9).</summary>
    public bool CanFillForms => (Permissions & ((1 << 5) | (1 << 8))) != 0;

    /// <summary>The fillable fields on a page, top to bottom in the file's own order.</summary>
    public Task<IReadOnlyList<PdfFormField>> GetFormFieldsAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run<IReadOnlyList<PdfFormField>>(() => WithFormPage(pageIndex, (form, page) =>
        {
            var fields = new List<PdfFormField>();
            int count = PdfiumNative.FPDFPage_GetAnnotCount(page);
            for (int i = 0; i < count; i++)
            {
                nint annotation = PdfiumNative.FPDFPage_GetAnnot(page, i);
                if (annotation == 0)
                {
                    continue;
                }

                try
                {
                    if (PdfiumNative.FPDFAnnot_GetSubtype(annotation) == PdfiumNative.FPDF_ANNOT_WIDGET && ReadField(form, page, annotation, pageIndex, i) is { } field)
                    {
                        fields.Add(field);
                    }
                }
                finally
                {
                    PdfiumNative.FPDFPage_CloseAnnot(annotation);
                }
            }

            return (IReadOnlyList<PdfFormField>)fields;
        }), cancellationToken);
    }

    /// <summary>Sets a text field, or an editable dropdown's text.</summary>
    public unsafe Task SetFieldTextAsync(int pageIndex, int fieldIndex, string text, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run(() => WithField(pageIndex, fieldIndex, (form, page, annotation) =>
        {
            _ = PdfiumNative.FORM_SetFocusedAnnot(form, annotation);
            _ = PdfiumNative.FORM_SelectAllText(form, page);
            fixed (char* value = text + '\0')
            {
                PdfiumNative.FORM_ReplaceSelection(form, page, value);
            }

            _ = PdfiumNative.FORM_ForceToKillFocus(form); // commits, and redraws the field's appearance
            HasAnnotationChanges = true;
            return true;
        }), cancellationToken);
    }

    /// <summary>Ticks or unticks a checkbox, or picks a radio button (which unpicks the others in its group).</summary>
    public unsafe Task SetFieldCheckedAsync(int pageIndex, int fieldIndex, bool isChecked, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run(() => WithField(pageIndex, fieldIndex, (form, page, annotation) =>
        {
            if ((PdfiumNative.FPDFAnnot_IsChecked(form, annotation) != 0) == isChecked)
            {
                return true;
            }

            // A click in its middle runs the field's own rules (radio groups, "no toggle to off").
            PdfiumNative.FS_RECTF rect;
            _ = PdfiumNative.FPDFAnnot_GetRect(annotation, &rect);
            double x = (rect.Left + rect.Right) / 2, y = (rect.Top + rect.Bottom) / 2;
            _ = PdfiumNative.FORM_OnLButtonDown(form, page, 0, x, y);
            _ = PdfiumNative.FORM_OnLButtonUp(form, page, 0, x, y);
            _ = PdfiumNative.FORM_ForceToKillFocus(form);
            HasAnnotationChanges = true;
            return true;
        }), cancellationToken);
    }

    /// <summary>Chooses an option in a dropdown or list.</summary>
    public Task SetFieldOptionAsync(int pageIndex, int fieldIndex, int option, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run(() => WithField(pageIndex, fieldIndex, (form, page, annotation) =>
        {
            _ = PdfiumNative.FORM_SetFocusedAnnot(form, annotation);
            _ = PdfiumNative.FORM_SetIndexSelected(form, page, option, 1);
            _ = PdfiumNative.FORM_ForceToKillFocus(form);
            HasAnnotationChanges = true;
            return true;
        }), cancellationToken);
    }

    private static unsafe PdfFormField? ReadField(nint form, nint page, nint annotation, int pageIndex, int index)
    {
        int type = PdfiumNative.FPDFAnnot_GetFormFieldType(form, annotation);
        PdfFormFieldKind? kind = type switch
        {
            PdfiumNative.FPDF_FORMFIELD_TEXTFIELD => PdfFormFieldKind.Text,
            PdfiumNative.FPDF_FORMFIELD_CHECKBOX => PdfFormFieldKind.CheckBox,
            PdfiumNative.FPDF_FORMFIELD_RADIOBUTTON => PdfFormFieldKind.RadioButton,
            PdfiumNative.FPDF_FORMFIELD_COMBOBOX => PdfFormFieldKind.ComboBox,
            PdfiumNative.FPDF_FORMFIELD_LISTBOX => PdfFormFieldKind.ListBox,
            PdfiumNative.FPDF_FORMFIELD_SIGNATURE => PdfFormFieldKind.Signature,
            PdfiumNative.FPDF_FORMFIELD_PUSHBUTTON => PdfFormFieldKind.Button,
            _ => null,
        };
        PdfiumNative.FS_RECTF rect;
        if (kind == null || PdfiumNative.FPDFAnnot_GetRect(annotation, &rect) == 0)
        {
            return null;
        }

        int flags = PdfiumNative.FPDFAnnot_GetFormFieldFlags(form, annotation);
        var options = new List<string>();
        int selected = -1;
        if (kind is PdfFormFieldKind.ComboBox or PdfFormFieldKind.ListBox)
        {
            int count = PdfiumNative.FPDFAnnot_GetOptionCount(form, annotation);
            for (int i = 0; i < count; i++)
            {
                int option = i;
                options.Add(ReadWide((b, n) => PdfiumNative.FPDFAnnot_GetOptionLabel(form, annotation, option, b, n)));
                if (PdfiumNative.FPDFAnnot_IsOptionSelected(form, annotation, i) != 0)
                {
                    selected = i;
                }
            }
        }

        return new PdfFormField(
            pageIndex,
            index,
            ReadWide((b, n) => PdfiumNative.FPDFAnnot_GetFormFieldName(form, annotation, b, n)),
            kind.Value,
            Normalize(page, rect.Left, rect.Top, rect.Right, rect.Bottom),
            ReadWide((b, n) => PdfiumNative.FPDFAnnot_GetFormFieldValue(form, annotation, b, n)),
            kind is PdfFormFieldKind.CheckBox or PdfFormFieldKind.RadioButton && PdfiumNative.FPDFAnnot_IsChecked(form, annotation) != 0,
            (flags & PdfiumNative.FPDF_FORMFLAG_READONLY) != 0,
            kind == PdfFormFieldKind.Text && (flags & PdfiumNative.FPDF_FORMFLAG_TEXT_MULTILINE) != 0,
            kind == PdfFormFieldKind.Text && (flags & PdfiumNative.FPDF_FORMFLAG_TEXT_PASSWORD) != 0,
            options,
            selected);
    }

    private unsafe delegate uint WideReader(char* buffer, uint bytes);

    private static unsafe string ReadWide(WideReader read)
    {
        uint bytes = read(null, 0);
        if (bytes <= 2)
        {
            return string.Empty;
        }

        var buffer = new char[bytes / 2];
        fixed (char* b = buffer)
        {
            _ = read(b, bytes);
        }

        return new string(buffer, 0, buffer.Length - 1);
    }

    private T WithField<T>(int pageIndex, int fieldIndex, Func<nint, nint, nint, T> work) => WithFormPage(pageIndex, (form, page) =>
    {
        nint annotation = PdfiumNative.FPDFPage_GetAnnot(page, fieldIndex);
        if (annotation == 0)
        {
            throw new InvalidOperationException($"Field {fieldIndex} on page {pageIndex + 1} is gone.");
        }

        try
        {
            return work(form, page, annotation);
        }
        finally
        {
            PdfiumNative.FPDFPage_CloseAnnot(annotation);
        }
    });

    /// <summary>Runs work with the form engine attached to a page of its own, closed again afterwards.</summary>
    private T WithFormPage<T>(int pageIndex, Func<nint, nint, T> work)
    {
        nint form = FormHandle();
        nint page = PdfiumNative.FPDF_LoadPage(_handle, pageIndex);
        if (page == 0)
        {
            throw new InvalidDataException($"Page {pageIndex + 1} of '{FilePath}' could not be loaded.");
        }

        PdfiumNative.FORM_OnAfterLoadPage(page, form);
        try
        {
            return work(form, page);
        }
        finally
        {
            PdfiumNative.FORM_OnBeforeClosePage(page, form);
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    /// <summary>
    /// The form engine for this document, started on first use. Its callback table is all empty on
    /// purpose: Papery draws its own controls, and PDFium checks each callback before using it.
    /// </summary>
    private unsafe nint FormHandle()
    {
        if (_form != 0)
        {
            return _form;
        }

        const int TableBytes = 1024; // bigger than FPDF_FORMFILLINFO in any PDFium version
        _formInfo = NativeMemory.AllocZeroed(TableBytes);
        *(int*)_formInfo = 1; // version
        _form = PdfiumNative.FPDFDOC_InitFormFillEnvironment(_handle, _formInfo);
        if (_form == 0)
        {
            throw new InvalidOperationException("The form engine could not start.");
        }

        return _form;
    }

    private unsafe void CloseForms()
    {
        if (_form != 0)
        {
            PdfiumNative.FPDFDOC_ExitFormFillEnvironment(_form);
            _form = 0;
        }

        if (_formInfo != null)
        {
            NativeMemory.Free(_formInfo);
            _formInfo = null;
        }
    }
}

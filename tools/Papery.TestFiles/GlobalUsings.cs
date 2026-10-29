global using System.Globalization;
global using PdfSharp.Drawing;
global using PdfSharp.Pdf;

// Both PDFsharp and Papery.Pdf have a "PdfDocument" type. In this tool, PDFsharp's
// (used to WRITE test files) is always called SharpDoc; Papery's engine (used to
// READ and verify them) is always written with its full Papery.Pdf name.
global using SharpDoc = PdfSharp.Pdf.PdfDocument;

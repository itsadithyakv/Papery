# Papery – PDF Editor, Merge & Sign

A fast, calm PDF app for Windows 10/11. Free to read; every other tool unlocks with a one-time
Papery Pro purchase. No subscription, no ads, no account, no cloud.

## Requirements

- Windows 10 1809 or later (x64 or ARM64)
- .NET 10 SDK (pinned in `global.json`)
- Visual Studio 2022/2026 with the **WinUI application development** workload, only for making the final Store package

## Layout

| Folder | What's inside |
|---|---|
| `src/Papery.App` | The WinUI 3 app (MSIX): window, tabs, home, viewer, dialogs. Look and feel: `Themes/Papery.xaml` |
| `src/Papery.Core` | Logic with no UI: page layout, zoom, print imposition, settings and saved positions |
| `src/Papery.Pdf` | PDF engine: PDFium bindings, all PDFium calls on one dedicated thread |
| `tests/` | Automated tests (xUnit v3) |
| `tools/Papery.TestFiles` | Generator for the `PaperyTestFiles` folder |
| `tools/Check-Licences.ps1` | Fails if any package has a non-permissive licence |
| `tools/Build-Release.ps1` | Makes the signed test MSIX packages and the EXE installer in `dist/` |
| `tools/Papery.Setup` | That EXE installer |
| `tools/Drive-Papery.ps1` | Works the app through accessibility, with no mouse (checks and screenshots) |
| `tools/Make-Branding.ps1`, `Make-Screenshots.ps1`, `Make-Icons.ps1` | Remake the art in `branding/` and the app's icons |
| `third_party/qpdf` | QPDF 12.4.2 (Apache-2.0), official builds for x64 and ARM64 |
| `branding/` | Logos, Store art, screenshots, listing text and privacy policy |
| `docs/` | Store setup guide, test checklist and third-party notices |

## Everyday commands

Build, register and start Papery (needs Windows **Developer Mode** on). Add `-File "C:\path\file.pdf"` to open a file:

```bash
powershell -ExecutionPolicy Bypass -File tools/Run-Papery.ps1
```

Once registered, `papery.exe some.pdf` works from any command line, and Papery appears in the Start menu
and in "Open with" for PDFs.

```bash
dotnet test --solution Papery.slnx
```

```bash
dotnet run --project tools/Papery.TestFiles -c Release
```

```bash
powershell -ExecutionPolicy Bypass -File tools/Check-Licences.ps1
```

## Licence rule

Papery is closed-source and sold on the Microsoft Store, so only MIT / BSD / Apache-2.0 code may be
used. **PDFsharp is pinned to 6.2.4**, its last MIT release: version 6.5.0 and later are AGPL.
Run `tools/Check-Licences.ps1` after changing any package.

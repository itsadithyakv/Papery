# Papery – final test checklist

Test files are in `PaperyTestFiles/` (run `tools/Papery.TestFiles` to remake them). Tick each one on your PC.

**Install first:** double-click `dist/Papery-Setup-x64.exe` and approve the admin prompt. It replaces the developer copy, so a fresh Papery starts with default settings.

The test build can't reach the Store, so Pro is locked in it. To test Pro tools, either run a developer build (`tools/Run-Papery.ps1`), where **Settings › Developer** can pretend you own Pro, or do the Store test in Part 7 of `docs/STORE-SETUP.md`.

## Your spec's list

- [ ] Open `long-1000-pages.pdf`: it should appear in under 2 seconds and scroll smoothly
- [ ] Edit and save a file, then open it in **Microsoft Edge** and **Adobe Acrobat Reader**
- [ ] Merge 20 files (`merge-set`), then check the page count
- [ ] Split by size
- [ ] Crop all pages
- [ ] Fill and sign `forms-interactive.pdf`, then open it in Edge and Adobe
- [ ] OCR `scanned-document.pdf`: search for "lighthouse" afterwards
- [ ] Compress to under 5 MB with **Fit under**
- [ ] Redact `redaction-test.pdf`: then search, and copy all the text, to confirm the words are gone
- [ ] Password-protect a file, and open it in Edge with the password
- [ ] Buy Pro, and restore it on another PC (needs the Store: Part 7 of the Store guide)
- [ ] Use Pro with Wi-Fi off
- [ ] Light, dark and High Contrast themes (Windows Settings › Accessibility › Contrast themes)
- [ ] 150% and 200% display scaling
- [ ] Touch and pen, if you have them: scroll, sign, annotate
- [ ] Keyboard only: Tab through a form, Ctrl+K, Ctrl+O, Ctrl+S, Ctrl+Z
- [ ] Narrator: Win+Ctrl+Enter, then move through the toolbar

## Things only you can check, because Windows blocked me

These all use a Windows file picker, a real device or an admin prompt, none of which I could operate. Please try each one, since none has been tried on screen:

- [ ] **The installer itself** (`Papery-Setup-x64.exe`): I checked it holds everything, but installing needs your admin approval
- [ ] **Explorer right-click**: right-click a PDF, then **Show more options** on Windows 11, for Merge, Compress, Convert to images. Right-click pictures for Combine into PDF
- [ ] **Taskbar**: right-click Papery's icon for your recent files
- [ ] **Add a picture** in Edit, by button and by dragging it in
- [ ] **Images to PDF**, with several photos in a chosen order
- [ ] **Save pages as pictures**, and **Save the pictures in this PDF**
- [ ] **Save as text** and **Save as Word**, and open the Word file in Word
- [ ] **Redact's save** (the dialog is checked, and the result is proven by tests)
- [ ] **Flatten** and **Pages per sheet**, which also end in a save picker
- [ ] **Merge's save**
- [ ] **Scan**: with a scanner, the camera (it takes one photo at once, with no live preview) and a photo
- [ ] **Add a password**: I couldn't type into the password box. The engine is tested, including opening with and without it
- [ ] **Certified digital signature**, if you have a certificate. It's tested with a temporary one, not checked in Adobe
- [ ] **Compare** two versions (`Tools › Compare`), since it starts with a file picker
- [ ] **Batch**, with real files
- [ ] **Crash recovery**: make a change, wait over a minute, end Papery in Task Manager, and reopen it
- [ ] **Change the original?** then **Always save a copy** in Settings
- [ ] **Damaged files**: open each `damaged-*.pdf`. Papery should repair it or explain why it can't, and never crash
- [ ] **ARM64**: on an ARM64 PC, if you have one, using `dist/Install-Papery.ps1`

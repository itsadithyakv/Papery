# Papery test file generator

Creates the `PaperyTestFiles` folder at the repo root: every PDF and image used for testing Papery.
The files are **not** committed to git (about 300 MB), so recreate them on any PC with:

```bash
dotnet run --project tools/Papery.TestFiles -c Release
```

Takes about 30 seconds. When it finishes it re-opens every file with Papery's own PDF engine and
prints `OK` or `FAIL` for each one, so you know the files are what they claim to be.

| Option | What it does |
|---|---|
| `--skip-large` | Skip the 1,000-page / 300 MB file (quick run) |
| `--verify-only` | Don't generate, just re-check the existing files |
| `--pages 2000 --large-mb 600` | Make the big file even bigger |
| `--out D:\Somewhere` | Write somewhere else |

The generated folder contains a `README.txt` describing every file. Passwords are `papery` (user) and
`papery-owner` (owner).

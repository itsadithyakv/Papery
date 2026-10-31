# Papery branding and Store kit

Everything for the Store listing, in one place.

| Folder | What's in it |
|---|---|
| `logo/` | The Papery logo at every size from 16 to 1024 pixels, plus `papery.ico` and `papery-logo.svg` |
| `store/` | Store listing art: 1:1 logos (300, 1080, 2160), 9:16 posters (720x1080, 1440x2160), 16:9 hero art (1920x1080, 3840x2160) |
| `screenshots/` | Screenshots from the real app, in dark and light, at 1800x1125 |
| `listing/store-listing.md` | Ready-to-paste name, descriptions, features, search terms, add-on text and certification notes |
| `listing/privacy-policy.html` | The privacy policy page. Put your support email in it, then host it (see `docs/STORE-SETUP.md`) |

Which art goes where in Partner Center (Store listings › Store logos and Screenshots):

- **1:1 Box art**: `store/StoreLogo-1x1-2160x2160.png` (also 1080)
- **9:16 Poster art**: `store/PosterArt-9x16-1440x2160.png` (also 720x1080)
- **16:9 Super hero art**: `store/HeroArt-16x9-3840x2160.png` (also 1920x1080)
- **Screenshots**: add up to 10 from `screenshots/`. Start with home, reading, organise, signing, filters, compress, merge.

Colours: coral `#E5533D` (light), `#FF7A63` (dark). Font: Segoe UI Variable.

To remake them: `tools/Make-Icons.ps1` (app icons), `tools/Make-Branding.ps1` (this folder's art), `tools/Make-Screenshots.ps1` (screenshots).

# Argonaut icon

`argonaut-icon.svg` is the source of truth — a golden fleece-shaped sail (with a
subtle `{ }` JSON motif) on the Argo, over a night sea. All other files here are
rendered from it and shouldn't be hand-edited; regenerate them from the SVG instead.

- **`argonaut.ico`** — Windows icon (16–256px, multi-resolution). Wired in via
  `<ApplicationIcon>` in `Argonaut.csproj`, which also makes Avalonia use it as the
  default window icon on every platform at runtime.
- **`argonaut.icns`** — macOS icon. Copied into `Argonaut.app/Contents/Resources` by
  `scripts/package-macos.sh` and referenced by `CFBundleIconFile` in `Argonaut/Info.plist`.
- **`linux/hicolor/...`** — the standard [Freedesktop icon theme](https://specifications.freedesktop.org/icon-theme-spec/icon-theme-spec-latest.html)
  layout (`hicolor/<size>x<size>/apps/argonaut.png`, plus a `scalable/apps/argonaut.svg`).
  Install by copying `linux/hicolor` into `~/.local/share/icons/hicolor` (per-user) or
  `/usr/share/icons/hicolor` (system-wide), then `gtk-update-icon-cache`.
- **`linux/argonaut.desktop`** — a starter `.desktop` entry referencing the icon by
  theme name (`Icon=argonaut`); adjust `Exec=` to wherever the published binary lands.
- **`argonaut-1024.png`** — flat 1024×1024 render, handy as a source for anything else
  (README badges, store listings, etc.) without re-rendering the SVG.

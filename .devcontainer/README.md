# Argonaut dev environment (Docker)

A full .NET 10 + Avalonia build environment with nothing installed on the host. The workflow is
the same on Windows, macOS and Linux — only the runtime identifier you publish for changes.

## Start it

```bash
docker compose up -d
```

Or open the folder in VS Code and pick **Reopen in Container** (install the
[Dev Containers](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers)
extension first). Both routes use this same `docker-compose.yml`, so the environment is identical
either way.

The first run builds the image and restores packages, then idles — Argonaut is a desktop app, not
a server, so there's nothing for it to hand a terminal over to.

## Everyday commands

You never have to be *inside* the container. These work as one-shots from your normal host shell —
PowerShell, Terminal.app, whatever — and are identical on every OS:

```bash
docker compose run --rm app dotnet build Argonaut.slnx
```

```bash
docker compose run --rm app dotnet test Argonaut.Tests/Argonaut.Tests.csproj
```

`run --rm` creates a throwaway container per command. If you'd rather keep one warm, the container
started by `docker compose up -d` is already there — swap `run --rm` for `exec`. And if you do want
a shell in it, `docker compose exec app bash`.

The tests use `Avalonia.Headless` and need no display.

## Build in the container, run it natively

This is the main loop: reproducible container builds, but a real native window on your own desktop —
no X server, no Xvfb, no GUI forwarding to configure.

```bash
docker compose run --rm app dotnet publish Argonaut/Argonaut.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Swap the RID for your host: `win-x64`, `osx-arm64`, `osx-x64`, or `linux-x64`. The binary lands in
the normal place, visible from the host:

    Argonaut/bin/Release/net10.0/<rid>/publish/

Run it from Explorer/Finder or your shell. A Linux container cross-compiles a complete Windows
binary — the `ApplicationIcon` and `app.manifest` are embedded in the PE resources, not silently
dropped.

**Note that container builds share `bin/` and `obj/` with your host builds** (Rider, Visual Studio,
`dotnet` on the host). They overwrite each other, and alternating between the two forces a re-restore,
since `obj/project.assets.json` records an absolute path to the NuGet cache that differs between host
and container. Harmless and self-healing, but if a build ever looks confused, `dotnet restore` or
deleting `bin`/`obj` sorts it out.

## If `dotnet run` fails

Two failures you can walk straight into, in order:

    Couldn't find a project to run. Ensure a project exists in /workspace

`/workspace` holds `Argonaut.slnx` and two project folders, no single `.csproj` at the root, so
`dotnet run` cannot pick one. Pass `--project Argonaut`.

    Unhandled exception. System.Exception: XOpenDisplay failed
       at Avalonia.X11.AvaloniaX11Platform.Initialize(X11PlatformOptions options)

Argonaut is a GUI app and the container has no display. Either publish and run natively (above),
which is the intended path, or prefix with `xvfb-run -a` for a startup-only smoke test.

## Seeing the UI inside the container

Usually unnecessary — publish natively as above instead. But if you want the app running *in* the
container, the image ships Xvfb:

```bash
docker compose run --rm app xvfb-run -a dotnet run --project Argonaut
```

That proves startup and catches XAML load failures, but you can't see or click anything. For a real
window, set `DISPLAY` in a `.env` file (copy `.env.example`) and restart:

- **Linux** — `DISPLAY=:0`. The X11 socket is already bind-mounted; you may need `xhost +local:docker`.
- **Windows** — run [VcXsrv](https://sourceforge.net/projects/vcxsrv/) or X410 with access control
  disabled, then `DISPLAY=host.docker.internal:0.0`.
- **macOS** — run XQuartz with *Allow connections from network clients* enabled, then
  `xhost + 127.0.0.1` and `DISPLAY=host.docker.internal:0`.

## What still needs a real Windows or macOS host

- **Velopack packaging and code signing** — `scripts/package-windows.ps1` and
  `scripts/package-macos.sh` shell out to `vpk`, Authenticode and `notarytool`. None cross-compile.
- **Genuinely platform-specific behaviour** — the memory-mapped-file capacity difference described in
  `CLAUDE.md` is exactly the kind of bug a Linux container will not reproduce.

## Working with large files

Argonaut exists to open multi-gigabyte files, and its JSON index can be larger than the file itself.

- **Give Docker enough RAM.** Docker Desktop's default VM memory is often below what a large JSON
  file needs; raise it in Settings → Resources if the app gets OOM-killed.
- **Don't put sample files on the bind mount** if you care about read throughput. Generate them
  inside the container (`scripts/make-test-json.py` and friends write wherever you point them), or
  add a named volume for them.

## How the Docker setup works

- **`docker-compose.yml` is the source of truth.** Setup happens in its `command:`, so
  `devcontainer.json` does nothing but point at the compose service — there's no separate
  `postCreateCommand` to drift out of sync. A `docker compose run`/`exec` command override *skips*
  that block, so nothing load-bearing lives there; anything a plain shell session needs is baked
  into the image.
- **The NuGet packages cache is a named volume** (`argonaut-nuget`), not part of the bind mount
  (`.:/workspace`). Restored packages stay out of your host OS, avoid slow Windows/WSL2 bind-mount
  I/O, and survive container rebuilds. Build output deliberately is *not* redirected — it goes to
  the usual `bin`/`obj` so anything you publish is simply there on the host.
- **`user: vscode`**: the base image runs as this non-root user, so files the container writes into
  the bind mount don't come back owned by root. The image pre-creates and `chown`s the volume mount
  point, because Docker otherwise creates a missing mount-point directory as `root` in every
  container — which is what breaks `exec`/`run`, not just `up`.
- **Base image is `mcr.microsoft.com/devcontainers/dotnet:1-10.0-noble`**, extended in
  `.devcontainer/Dockerfile` with Avalonia's X11/GL/fontconfig runtime dependencies and Xvfb — the
  stock image has no GUI stack at all. Two upstream quirks are worked around there: it ships a yarn
  apt repo whose signing key is missing (which makes `apt-get update` fail outright), and its SDK is
  still `10.0.100-rc.2`, so the GA SDK is installed alongside to match
  `.github/workflows/publish.yml`'s `dotnet-version: 10.0.x`.

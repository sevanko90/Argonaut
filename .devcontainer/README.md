# Argonaut dev environment (Docker)

This container gives you a full .NET 10 + Avalonia build environment with nothing installed on
the host. Two ways in, both driven by the same `docker-compose.yml`, so they behave identically:

| Method                    | Best for                                                                                  |
| ------------------------- | ----------------------------------------------------------------------------------------- |
| **VS Code Dev Container** | Full IDE integration — extensions, settings, and the terminal all run inside the container |
| **`docker compose up`**   | Any editor, or headless / CI use                                                            |

## Option 1 — VS Code Dev Container

1. Install the [Dev Containers](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers) extension.
2. Open this folder in VS Code.
3. Click **Reopen in Container** when prompted.

## Option 2 — `docker compose up`

```bash
docker compose up
```

The first run builds the image (a few hundred MB of base image, so give it a minute), restores
packages, then idles. Argonaut is a desktop app, not a server, so there's nothing for the
container to hand its terminal over to — you work from a shell inside it:

```bash
docker compose exec app bash
```

From there:

```bash
dotnet build Argonaut.slnx
dotnet test Argonaut.Tests/Argonaut.Tests.csproj
dotnet run --project Argonaut          # needs a display, see below
```

The unit tests use `Avalonia.Headless` and need no display — they run in the bare container.

## Seeing the UI

The image ships Xvfb, so you can start the real app with no X server anywhere:

```bash
xvfb-run -a dotnet run --project Argonaut
```

That's enough to prove startup and catch XAML load failures, but you can't see or click
anything. To get a real window on your desktop, set `DISPLAY` in a `.env` file (copy
`.env.example`) and restart the container:

- **Linux** — `DISPLAY=:0`. The X11 socket is already bind-mounted; you may need
  `xhost +local:docker` once.
- **Windows** — run [VcXsrv](https://sourceforge.net/projects/vcxsrv/) or X410 with access
  control disabled, then `DISPLAY=host.docker.internal:0.0`.
- **macOS** — run XQuartz with *Allow connections from network clients* enabled, then
  `xhost + 127.0.0.1` and `DISPLAY=host.docker.internal:0`.

Only the Linux (X11) backend runs in here. Windows and macOS builds still have to be built and
tested on those hosts — as does anything touching Velopack packaging (`scripts/package-*`).

## Working with large files

Argonaut exists to open multi-gigabyte files, and its JSON index can be larger than the file
itself. Two things follow:

- **Give Docker enough RAM.** Docker Desktop's default VM memory is often below what a large
  JSON file needs; raise it in Settings → Resources if the app gets OOM-killed.
- **Don't put sample files on the bind mount** if you care about read throughput on
  Windows/macOS. Generate them inside the container (`scripts/make-test-json.py` and friends
  write wherever you point them), or add a named volume:

  ```yaml
  volumes:
    - argonaut-samples:/samples
  ```

## How the Docker setup works

- **`docker-compose.yml` is the source of truth.** Setup happens in its `command:`, so
  `devcontainer.json` does nothing but point at the compose service — there's no separate
  `postCreateCommand` to drift out of sync. Note that a `docker compose run`/`exec` command
  override *skips* that block, so nothing load-bearing lives there; anything a plain shell
  session needs is baked into the image instead.
- **The NuGet packages cache is a named volume** (`argonaut-nuget`), not part of the bind mount
  (`.:/workspace`). Restored packages stay out of your host OS, avoid slow Windows/WSL2
  bind-mount I/O, and survive container rebuilds.
- **Build output is a named volume too** (`argonaut-artifacts`, via the `ArtifactsPath`
  environment variable — MSBuild reads properties from the environment, so no `.csproj` was
  touched). The default `bin/`/`obj/` sit on the bind mount and would be shared with your host
  builds; a Rider build on Windows left there makes the container trip over foreign binaries —
  VSTest rejects a `win-x64` test dll outright. Inside the container, build output lands in
  `/home/vscode/artifacts/bin/<project>/debug/`, and the repo's own `bin/` stays purely yours.
- **`user: vscode`**: the base image runs as this non-root user, so files the container writes
  into the bind mount don't come back owned by root. The image pre-creates and `chown`s the two
  volume mount points, because Docker otherwise creates a missing mount-point directory as
  `root` in every container — which is what breaks `exec`/`run`, not just `up`.
- **Base image is `mcr.microsoft.com/devcontainers/dotnet:1-10.0-noble`**, extended in
  `.devcontainer/Dockerfile` with Avalonia's X11/GL/fontconfig runtime dependencies and Xvfb —
  the stock image has no GUI stack at all. Two upstream quirks are worked around there: it
  ships a yarn apt repo whose signing key is missing (which makes `apt-get update` fail
  outright), and its SDK is still `10.0.100-rc.2`, so the GA SDK is installed alongside to match
  `.github/workflows/publish.yml`'s `dotnet-version: 10.0.x`.

## Verified

As of the commit that added this, inside the container: `dotnet --version` → `10.0.400`,
`dotnet build Argonaut.slnx` clean, `dotnet test` → 842 passed / 0 failed, and
`xvfb-run -a dotnet run --project Argonaut` brings the real window up and keeps it up.

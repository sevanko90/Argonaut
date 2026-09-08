# .NET 10 SDK on Ubuntu Noble, with the devcontainers non-root `vscode` user.
# The stock bootstrap preset image is dotnet:8.0; Argonaut targets net10.0, so it needs this tag.
FROM mcr.microsoft.com/devcontainers/dotnet:1-10.0-noble

# Argonaut is an Avalonia desktop app. The SDK image has no GUI stack, so add the
# X11 / font / GL libraries Avalonia's Linux backend links against, plus Xvfb so the
# real window can be driven headlessly inside the container (see .devcontainer/README.md).
# Avalonia.Headless (used by Argonaut.Tests) needs fontconfig and a font to shape text,
# even though it never opens a window.
#
# The base image ships a yarn apt repo whose signing key it no longer carries, which makes
# `apt-get update` fail outright (NO_PUBKEY 62D54FD4003F6525). Nothing here comes from yarn,
# so park that list for the duration of the update and put it back afterwards, rather than
# trusting a third-party key we don't need.
RUN if [ -f /etc/apt/sources.list.d/yarn.list ]; then mv /etc/apt/sources.list.d/yarn.list /tmp/yarn.list.bak; fi \
    && apt-get update && export DEBIAN_FRONTEND=noninteractive \
    && apt-get install -y --no-install-recommends \
        libx11-6 \
        libxext6 \
        libxrandr2 \
        libxi6 \
        libxcursor1 \
        libice6 \
        libsm6 \
        libxkbcommon-x11-0 \
        libgl1 \
        libglx-mesa0 \
        libegl1 \
        fontconfig \
        fonts-dejavu-core \
        xvfb \
    && if [ -f /tmp/yarn.list.bak ]; then mv /tmp/yarn.list.bak /etc/apt/sources.list.d/yarn.list; fi \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Pre-create the NuGet volume mount point, owned by `vscode`.
# Docker creates a missing mount-point directory as root in *every* container, and seeds a
# fresh named volume from whatever the image has at that path - so doing it here is what makes
# `docker compose exec/run` work, not just `docker compose up`. (The preset repairs this at
# runtime in compose's `command:`, which a command override skips.)
RUN mkdir -p /home/vscode/.nuget/packages \
    && chown -R vscode:vscode /home/vscode/.nuget

# The devcontainers .NET 10 image is still on an RC SDK (10.0.100-rc.2), which drifts from
# .github/workflows/publish.yml's `dotnet-version: 10.0.x` and makes the build emit NETSDK1057.
# Install the GA SDK alongside it in the same root; `dotnet` then selects the highest it finds.
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet --no-path \
    && rm /tmp/dotnet-install.sh \
    && dotnet --version

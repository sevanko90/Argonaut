#!/usr/bin/env python3
"""Builds THIRD-PARTY-NOTICES.txt: the licence and copyright notices for everything Argonaut ships.

The file is embedded in the app (Help > About > Licenses and third-party notices) and copied next to
the published binaries, because MIT, BSD, OFL and the Unicode License all require their notices to
travel with binary copies. See Argonaut/Infrastructure/LicenseNotices.cs for the reader.

Run it deliberately, not from the build, whenever a package is added or its version moves:

    dotnet restore Argonaut/Argonaut.csproj
    python3 scripts/make-third-party-notices.py

Package-supplied notices (SkiaSharp's native libraries, the .NET runtime's notices, ANGLE) are read
from the NuGet cache at the versions resolved in Argonaut/obj/project.assets.json. Licences that
upstream publishes only in its repository are downloaded. The script fails if a shipped package is
not covered by any section below, so a new dependency cannot slip in unattributed.

Output format, which LicenseNotices.Parse relies on - one section per component:

    ================================================================================
    Component: <name>
    Homepage: <url>
    Packages: <id> <version>, ...          (optional)
    ================================================================================
    <notice text>
"""

from __future__ import annotations

import json
import pathlib
import re
import sys
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
ASSETS = ROOT / "Argonaut" / "obj" / "project.assets.json"
OUTPUT = ROOT / "THIRD-PARTY-NOTICES.txt"
RULE = "=" * 80

# Packages that never reach a user: DiagnosticsSupport is excluded from non-Debug builds in
# Argonaut.csproj, BuildServices is an MSBuild-time package.
BUILD_ONLY = {"AvaloniaUI.DiagnosticsSupport", "Avalonia.BuildServices"}

# The .NET runtime's notices ship in every package built from dotnet/runtime. They are repo-wide
# rather than per-package, and older packages list a few entries newer ones dropped, so every such
# package's blocks are merged by title rather than one file being picked.
RUNTIME_NOTICE_PACKAGES = [
    ("System.IO.Hashing", "THIRD-PARTY-NOTICES.TXT"),
    ("Microsoft.Extensions.DependencyInjection.Abstractions", "THIRD-PARTY-NOTICES.TXT"),
    ("Microsoft.Extensions.Logging.Abstractions", "THIRD-PARTY-NOTICES.TXT"),
]

FREETYPE_CREDIT = (
    "Portions of this software are copyright (c) The FreeType Project (https://freetype.org).\n"
    "All rights reserved.\n"
)

UNICODE_TRADEMARK = (
    "Unicode and the Unicode Logo are registered trademarks of Unicode, Inc. in the United States\n"
    "and other countries.\n"
)

# Each section: the component, its homepage, which packages it covers (exact ids, or a prefix
# ending in '.'), and the parts its notice text is assembled from, in order.
SECTIONS = [
    {
        "component": "Avalonia",
        "homepage": "https://github.com/AvaloniaUI/Avalonia",
        "packages": ["Avalonia", "Avalonia."],
        "parts": [("fetch", "https://raw.githubusercontent.com/AvaloniaUI/Avalonia/master/licence.md")],
    },
    {
        "component": "Inter font",
        "homepage": "https://github.com/rsms/inter",
        "packages": [],
        "parts": [
            ("literal", "Embedded in Avalonia.Fonts.Inter and used as the interface font.\n"),
            ("fetch", "https://raw.githubusercontent.com/rsms/inter/master/LICENSE.txt"),
        ],
    },
    {
        "component": "SkiaSharp and HarfBuzzSharp",
        "homepage": "https://github.com/mono/SkiaSharp",
        "packages": ["SkiaSharp", "SkiaSharp.", "HarfBuzzSharp", "HarfBuzzSharp."],
        "parts": [
            ("package", "SkiaSharp", "LICENSE.txt"),
            ("literal", FREETYPE_CREDIT),
            ("package_same", ["SkiaSharp.NativeAssets.macOS", "SkiaSharp.NativeAssets.Win32",
                              "SkiaSharp.NativeAssets.Linux", "HarfBuzzSharp.NativeAssets.macOS",
                              "HarfBuzzSharp.NativeAssets.Win32", "HarfBuzzSharp.NativeAssets.Linux"],
             "THIRD-PARTY-NOTICES.txt"),
        ],
    },
    {
        "component": "ANGLE (Windows)",
        "homepage": "https://github.com/google/angle",
        "packages": ["Avalonia.Angle.Windows.Natives"],
        "parts": [("package", "Avalonia.Angle.Windows.Natives", "LICENSE")],
    },
    {
        "component": ".NET Runtime",
        "homepage": "https://github.com/dotnet/runtime",
        "packages": ["System.IO.Hashing", "Microsoft.Extensions."],
        "parts": [
            ("literal", "Argonaut is published self-contained, so the .NET runtime ships with it.\n"),
            ("fetch", "https://raw.githubusercontent.com/dotnet/runtime/main/LICENSE.TXT"),
            ("runtime_notices",),
        ],
    },
    {
        "component": "Microsoft.IO.RecyclableMemoryStream",
        "homepage": "https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream",
        "packages": ["Microsoft.IO.RecyclableMemoryStream"],
        "parts": [("fetch", "https://raw.githubusercontent.com/microsoft/Microsoft.IO.RecyclableMemoryStream/master/LICENSE")],
    },
    {
        "component": "MicroCom",
        "homepage": "https://github.com/kekekeks/MicroCom",
        "packages": ["MicroCom.Runtime"],
        "parts": [("fetch", "https://raw.githubusercontent.com/kekekeks/MicroCom/master/LICENSE")],
    },
    {
        "component": "Tmds.DBus",
        "homepage": "https://github.com/tmds/Tmds.DBus",
        "packages": ["Tmds.DBus.Protocol"],
        "parts": [("fetch", "https://raw.githubusercontent.com/tmds/Tmds.DBus/main/COPYING")],
    },
    {
        "component": "Velopack",
        "homepage": "https://github.com/velopack/velopack",
        "packages": ["Velopack"],
        "parts": [
            ("literal", "Covers the Velopack library and the native updater it installs alongside the app.\n"),
            ("fetch", "https://raw.githubusercontent.com/velopack/velopack/develop/LICENSE"),
        ],
    },
    {
        "component": "Unicode Character Database",
        "homepage": "https://www.unicode.org/ucd/",
        "packages": [],
        "parts": [
            ("literal", "Character names shown in the raw view are generated from the Unicode Character\n"
                        "Database (see Argonaut/Assets/Unicode/README.md).\n"),
            ("fetch", "https://www.unicode.org/license.txt"),
            ("literal", UNICODE_TRADEMARK),
        ],
    },
]


def normalise(text: str) -> str:
    """LF endings, no BOM, no trailing whitespace, exactly one final newline."""
    text = text.lstrip("﻿").replace("\r\n", "\n").replace("\r", "\n")
    lines = [line.rstrip() for line in text.split("\n")]
    return "\n".join(lines).strip("\n") + "\n"


def fetch(url: str) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": "argonaut-notices"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.read().decode("utf-8")


def resolved_packages() -> tuple[pathlib.Path, dict[str, str]]:
    assets = json.loads(ASSETS.read_text(encoding="utf-8"))
    packages_path = pathlib.Path(assets["project"]["restore"]["packagesPath"])
    versions = {}
    for key, library in assets["libraries"].items():
        if library["type"] == "package":
            name, version = key.split("/", 1)
            versions[name] = version
    return packages_path, versions


def package_file(packages_path: pathlib.Path, versions: dict[str, str], name: str, file: str) -> str:
    if name not in versions:
        sys.exit(f"{name} is not a resolved package - run dotnet restore, or update this script.")
    folder = packages_path / name.lower() / versions[name].lower()
    matches = [p for p in folder.iterdir() if p.name.lower() == file.lower()]
    if not matches:
        sys.exit(f"{name} {versions[name]} has no {file}.")
    return matches[0].read_text(encoding="utf-8-sig")


def runtime_notices(packages_path: pathlib.Path, versions: dict[str, str]) -> str:
    """Merges the repo-wide notice files, first file's order and preamble, later files adding
    only blocks whose 'License notice for ...' title the earlier ones lacked."""
    preamble = None
    blocks: dict[str, str] = {}
    for name, file in RUNTIME_NOTICE_PACKAGES:
        text = normalise(package_file(packages_path, versions, name, file))
        head, *rest = re.split(r"(?m)^(?=License notice for )", text)
        if preamble is None:
            preamble = head
        for block in rest:
            title = block.split("\n", 1)[0].strip()
            blocks.setdefault(title, normalise(block))
    return preamble + "\n" + "\n".join(blocks.values())


def matches(patterns: list[str], package: str) -> bool:
    return any(package == p or (p.endswith(".") and package.startswith(p)) for p in patterns)


def covers(section: dict, package: str) -> bool:
    """A package belongs to the first section that matches it, so a specific section listed later
    (ANGLE's natives) still wins over a broad prefix listed earlier (Avalonia.) - it is excluded
    from every section before the one naming it exactly."""
    for candidate in SECTIONS:
        if package in candidate["packages"]:
            return candidate is section
    first = next((s for s in SECTIONS if matches(s["packages"], package)), None)
    return first is section


def build_section(section: dict, packages_path: pathlib.Path, versions: dict[str, str]) -> str:
    parts = []
    for part in section["parts"]:
        kind = part[0]
        if kind == "literal":
            parts.append(part[1])
        elif kind == "fetch":
            parts.append(fetch(part[1]))
        elif kind == "package":
            parts.append(package_file(packages_path, versions, part[1], part[2]))
        elif kind == "package_same":
            texts = {name: normalise(package_file(packages_path, versions, name, part[2])) for name in part[1]}
            if len(set(texts.values())) != 1:
                sys.exit(f"{part[2]} differs between {', '.join(part[1])} - list them separately.")
            parts.append(next(iter(texts.values())))
        elif kind == "runtime_notices":
            parts.append(runtime_notices(packages_path, versions))

    header = [RULE, f"Component: {section['component']}", f"Homepage: {section['homepage']}"]
    covered = sorted(name for name in versions if name not in BUILD_ONLY and covers(section, name))
    if covered:
        header.append("Packages: " + ", ".join(f"{name} {versions[name]}" for name in covered))
    header.append(RULE)
    return "\n".join(header) + "\n\n" + "\n".join(normalise(p) for p in parts)


def main() -> None:
    packages_path, versions = resolved_packages()

    uncovered = [name for name in versions
                 if name not in BUILD_ONLY and not any(covers(s, name) for s in SECTIONS)]
    if uncovered:
        sys.exit("No notice section covers: " + ", ".join(sorted(uncovered)))

    sections = [build_section(s, packages_path, versions) for s in SECTIONS]
    # Bytes, not write_text: every part is already LF-normalised, and text mode would translate
    # to CRLF on Windows.
    OUTPUT.write_bytes("\n".join(sections).encode("utf-8"))
    print(f"Wrote {OUTPUT.relative_to(ROOT)} ({OUTPUT.stat().st_size:,} bytes, {len(sections)} sections)")


if __name__ == "__main__":
    main()

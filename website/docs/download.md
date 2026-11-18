# Download

Argonaut is free and open source. Pick your platform:

<div class="grid cards" markdown>

-   :material-microsoft-windows:{ .lg .middle } __Windows__

    ---

    64-bit installer. Keeps itself up to date.

    [Download for Windows](https://github.com/sevanko90/Argonaut/releases/latest/download/Argonaut-win-Setup.exe){ .md-button .md-button--primary }

-   :material-apple:{ .lg .middle } __macOS__

    ---

    Apple silicon. Signed and notarised installer. Keeps itself up to date.

    [Download for macOS](https://github.com/sevanko90/Argonaut/releases/latest/download/Argonaut-osx-Setup.pkg){ .md-button .md-button--primary }

-   :material-linux:{ .lg .middle } __Linux__

    ---

    64-bit, as a zip. Unpack it and run `Argonaut`.

    [Download for Linux](https://github.com/sevanko90/Argonaut/releases/latest/download/argonaut-linux-x64.zip){ .md-button .md-button--primary }

</div>

Every version, with what changed in it, is on the
[releases page](https://github.com/sevanko90/Argonaut/releases).

## Updates

On Windows and macOS, Argonaut checks for a new version when it starts - at most once a day - and
offers to install it. Choose **Restart to update** and it comes back on the new version. You can
turn the check off in the About box.

The Linux build doesn't update itself yet: download the new zip from the releases page.

!!! warning "On macOS 2.3.1 to 2.4.0? Reinstall once"

    Those versions couldn't install updates: **Restart to update** closed the app without
    updating. Download and install the latest version by hand once, and updates work normally
    from then on. API keys saved in requests by those versions need entering again.

## Building it yourself

Argonaut is a .NET application. With the [.NET SDK](https://dotnet.microsoft.com/download)
installed, clone the [repository](https://github.com/sevanko90/Argonaut) and run

```
dotnet publish Argonaut
```

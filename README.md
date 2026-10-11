# SF6Access

Accessibility mod for **Street Fighter 6** — a REFramework plugin that reads the game's menus and
World Tour out loud through a screen reader, so the game can be played without sight.

## Download

**https://github.com/vkvk86/sf6Access/releases/latest/download/SF6Access.zip**

That link always points at the newest release. Install REFramework first.

## Install

1. Install [REFramework](https://www.nexusmods.com/streetfighter6/mods/1?tab=files) for Street Fighter 6
   (the mod is a REFramework .NET plugin, so REFramework itself is required).
2. Unzip `SF6Access.zip` into the game folder:
   `...\Steam\steamapps\common\Street Fighter 6\`
   The `reframework` folder inside the zip merges with the one already there:
   ```
   reframework\plugins\managed\SF6Access.dll
   reframework\plugins\managed\SF6Access.lang\*.txt
   reframework\plugins\managed\SF6Access.sounds\*.mp3
   reframework\plugins\managed\dependencies\NAudio*.dll
   reframework\plugins\managed\dependencies\TolkDotNet.dll
   ```
3. Start the game. REFramework loads plugins only at startup, so **restart the game after every update**
   (a new DLL will not take effect on a running game).

If a menu stays silent, press **F8** in game to write a dump file and send it in — that is what makes
fixes possible.

## Changes

See [CHANGELOG.md](CHANGELOG.md).

## Building from source

The build needs the local game install (REFramework.NET assemblies) plus `references/tolk`, neither of
which are in the repository — so releases are packaged on the author's machine, not by CI:

```bash
python tools/make-release.py --version v0.6.0 --publish --notes-file notes.md
```

Without `--publish` it only builds `release/SF6Access.zip`. Requires the .NET 10 SDK and
`dotnet build -c Release -p:SF6Path="<game folder>"` to succeed.

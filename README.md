# ffxiv-addons

Each addon is kept separately: it has its own folder with its own code, its own page, its own releases, and its own install link. Installing one never pulls in another.

## Addons

| Addon | What it does | Install link |
| --- | --- | --- |
| [Hunting Log Plus](HuntingLogPlus/README.md) | Map flags, sorting and a full to-do list, built into the game's Hunting Log. | `https://raw.githubusercontent.com/jakeywunder/ffxiv-addons/main/HuntingLogPlus/repo.json` |

## Installing an addon

1. In game, run `/xlsettings` and open the Experimental tab.
2. Under "Custom Plugin Repositories", paste the addon's install link from the table above, tick the box next to it, and save.
3. Run `/xlplugins`, search for the addon by name, and install it.

Updates then arrive through the plugin installer like any other plugin. Each addon needs its own link.

## Layout

- `<AddonName>/` holds one addon: its source, its `README.md`, and its `repo.json` (the file its install link points at).
- `KamiToolKit/` is a shared library for building native game UI, not an addon. It is a git submodule.
- `publish.ps1` publishes a new version of one addon.

## Building

Needs the .NET 10 SDK and a Dalamud install (XIVLauncher).

```bash
git clone --recurse-submodules https://github.com/jakeywunder/ffxiv-addons.git
```

```bash
dotnet build HuntingLogPlus/HuntingLogPlus.csproj -c Release
```

The build puts the addon in `<AddonName>/bin/Release/`. To load a local build, add the full path of its `.dll` under "Dev Plugin Locations" in `/xlsettings` > Experimental, then enable it under Dev Tools > Installed Dev Plugins in `/xlplugins`. Don't have the same addon installed both ways at once.

## Publishing a new version of an addon

1. Bump `<Version>` in the addon's `.csproj`, commit, and push.
2. Run the publish script for that addon. It builds, uploads the zip as a GitHub release named `<AddonName>-v<version>`, and rewrites `<AddonName>/repo.json`:

```bash
powershell -ExecutionPolicy Bypass -File publish.ps1 -Plugin HuntingLogPlus
```

3. Commit and push `<AddonName>/repo.json`.

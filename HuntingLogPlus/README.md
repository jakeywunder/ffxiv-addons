# Hunting Log Plus

Builds map flags, sorting and a full to-do list into the game's Hunting Log, so you can track down your targets without opening a separate window.

## Install link

```
https://raw.githubusercontent.com/jakeywunder/ffxiv-addons/main/HuntingLogPlus/repo.json
```

Paste it under "Custom Plugin Repositories" in `/xlsettings` > Experimental, tick the box, save, then install Hunting Log Plus from `/xlplugins`.

## What it does

- Click a monster's icon in the Hunting Log to flag it on the map. Shift+Click also teleports to the nearest aetheryte (needs the Teleporter plugin). Dungeon monsters open the Duty Finder.
- Tick "All remaining" for one list of every target you still need, sorted by zone or rank.
- "All classes" includes every class you have unlocked. Without it, the list covers your current class and Grand Company. Faded targets belong to a class you aren't playing right now.
- "Nearest first" puts targets in your current zone on top by distance, then other zones, then dungeons.
- "Auto-flag next" flags the next nearest target when you finish one.

## Credits

Completely inspired by the amazing work done by [Hunty](https://github.com/Infiziert90/Hunty) by Infi, of whom the monster data also comes from. And a big thank you to [KamiToolKit](https://github.com/MidoriKami/KamiToolKit) by MidoriKami for the native UI used in this addon.

## Notes for working on it

- The Hunting Log window is the addon `MonsterNote`; its layout file is `ui/uld/MonsterNoteBook.uld`. Node ids in `Ui/HuntingLogController.cs` come from that file.
- KamiToolKit is pinned to the commit VanillaPlus 1.4.5.13 shipped with, which is known to work on Dalamud API 15. Check a current VanillaPlus release before bumping it.
- `/hlplus debug` logs Hunting Log events to `/xllog`, for diagnosing icon clicks.

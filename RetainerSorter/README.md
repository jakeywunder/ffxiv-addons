# Retainer Sorter

Sorts and rearranges items across all your retainers by rules you choose: materia on one, materials on the next few in level order, gear on another, and so on. Duplicates always end up together.

## Install link

```
https://raw.githubusercontent.com/jakeywunder/ffxiv-addons/main/RetainerSorter/repo.json
```

Paste it under "Custom Plugin Repositories" in `/xlsettings` > Experimental, tick the box, save, then install Retainer Sorter from `/xlplugins`.

## How to use it

1. Go to a summoning bell and open the retainer list. Press the **Sort Retainers** button at the bottom right of that window (or type `/rsort`).
2. On the **Rules** tab, pick a rule and tick the retainers that should hold those items. A rule does nothing until at least one retainer is ticked.
3. On the **Sort** tab, press **Scan retainers** the first time. It opens each retainer once to read its inventory and moves nothing.
4. Read the preview: every retainer, how full it is now and after, and what it will hold. Nothing has moved yet.
5. Press **Execute**. Press **Stop** (or type `/rsort stop`) at any moment.

Keep as many inventory slots free as you can. Free slots are the carrying space, so more of them means fewer trips. Two is the minimum.

## What it does

- **Duplicates together.** Each item ends up on one retainer, merged into as few stacks as the game allows. High-quality and normal-quality stacks stay separate, as the game requires, but sit on the same retainer. This happens even with no rules at all.
- **Rules, checked top to bottom.** The first rule an item matches decides where it goes. A rule picks items by category (the game's own categories, plus groups such as "Materials (all)" and "Gear (all)") and, optionally, by part of the name.
- **Several retainers per rule.** They are filled in list order. "Level gathered or crafted at" uses the gathering level, then the recipe level, and for everything else (monster drops, vendor goods, gear) a level worked out from the item level, so low-level materials land on the first retainer and high-level ones on the last.
- **Overflow.** When a rule's retainers are full, the rest goes to the next retainer with room.
- **Unmatched items** go to the retainers ticked "Also takes unmatched items" on the Options tab. If none is ticked, each stays on the retainer that already holds the most of it.
- **Leave alone.** Tick "Leave these items wherever they are" on a rule to protect items from the rules below it. Tick "Never open" on a retainer to keep the sorter out of it completely.
- **Tidy runs.** Run it again later and only new or misplaced items move.
- **Profiles.** Keep several rule sets. Export copies one to the clipboard as text; Import adds one from the clipboard. Rules refer to retainers by their place in the list, so a profile works for anyone.

## Deposit

The **Deposit Items** button on the retainer list (or the Deposit tab) puts items from your inventory with the retainer that already holds that item, across all your retainers at once. It is the game's "Entrust Duplicates", without opening each retainer yourself.

- It first lists what would go where. Nothing moves until you press **Deposit**.
- **Click an item in the list to keep it with you.** It turns grey and says "kept with you", and the choice is remembered, so the food and potions you carry stay put. Click it again to undo.
- Only what is in the list when you press Deposit is moved. Nothing is ever taken out of a retainer.
- An item goes onto a matching stack if one has room, otherwise into an empty slot on that retainer. If that retainer is full, the item stays in your inventory and the list says so beforehand.
- Not deposited: unique items (the retainer already has its one copy), items a rule says to leave wherever they are, and anything on a retainer marked "Never open".

## Retainer comments

Tick "Write each retainer's comment from what it holds" on the Options tab and the comment shown beside each retainer in the retainer list (the game's "Edit retainer comment") is kept up to date for you: "Materia", "Materials 1-23", "Housing, Misc".

- It names kinds of item, never single items, biggest share first, within the game's 30 characters. Kinds that don't fit become "+2".
- The kinds are your own rules' categories. Items no rule covers fall under broad ones: Materia, Materials, Gear, Housing, Food, Medicine, Dyes, Collectibles, Ship parts, Misc.
- When a rule fills several retainers, each comment says which part that retainer got: the level range ("Materials 50-60"), item level range ("Gear i400-560") or first letters ("Housing A-M"), matching how the rule is ordered.
- Comments are rewritten after every scan, sort and deposit, and when you close a retainer you rearranged by hand.
- It replaces comments you wrote yourself. Retainers marked "Never open" keep theirs.

## What it never touches

- A sort never touches your own inventory items: only empty inventory slots are used, for carrying. Deposit is the one thing that moves your own items, and only the ones it lists.
- Your saddlebag, your armoury chest, and what you are wearing.
- Items listed on the market, a retainer's own equipped gear, crystals, and gil.

## Unique items

The game allows only one copy of a unique item per retainer, and per inventory. So:

- If you keep several copies (spare gear coffers, for example), each retainer the rule points at gets one copy and the others stay where they are. The preview lists these. Tick a second retainer for that rule, or a second "Also takes unmatched items", to give the other copies a home too.
- A copy cannot pass through your inventory while you are carrying one yourself, so it stays put until you are not.

## Stopping and resuming

Stop finishes the move in progress and halts. Items being carried at that moment stay in your inventory, and the sorter remembers which slots are its own. Press Execute again and it delivers them first, then carries on. This also holds after a crash or a disconnect, except for anything picked up in the final second, which is then left in your inventory like any other item.

Every move is decided from what is actually in the inventories at that moment, so moving things by hand between runs is fine.

## AutoRetainer

Both plugins drive the retainer list, so they take turns:

- While a retainer is being sorted, AutoRetainer is told to hold off.
- Whenever AutoRetainer is busy, the sorter waits.
- With "Step aside for AutoRetainer when ventures finish" ticked (Options tab, on by default), the sorter pauses between two retainers when a venture has finished, lets AutoRetainer collect and reassign, then continues. If AutoRetainer is set to close the retainer list when it is done, the sort stops there; open the list and press Execute to continue.

## Credits

A big thank you to [KamiToolKit](https://github.com/MidoriKami/KamiToolKit) by MidoriKami for the native UI used in this addon, and to [AutoRetainer](https://github.com/PunishXIV/AutoRetainer) by NightmareXIV and kawaii, whose way of opening and closing retainers this addon follows.

## Notes for working on it

- `Sorting/` is plain logic with no game access: `Planner` decides which retainer every stack belongs on, `Router` picks the next single move and the next retainer to open, and `Simulator` plays a whole sort on a copy for the preview. It can be compiled and run outside the game against the real item sheets.
- `Game/Executor.cs` is the step-by-step loop. It writes each step to the Dalamud log at debug level (`/xllog`), which is the first place to look if a run stops unexpectedly.
- `Sorting/Depositor.cs` is the deposit logic, equally free of game access.
- The retainer list is the addon `RetainerList` (layout `ui/uld/RetainerList.uld`); the button positions in `Ui/RetainerListButtons.cs` come from that file. Retainer rows are read from the window's values: ten per retainer starting at the fourth, name first.
- Menu entries are matched by the game's own text (`Addon` sheet rows 2378 and 2383), so it follows the client language.
- Retainer contents are saved to `retainers.json` in the plugin's config folder, because the game only knows a retainer's inventory while it is summoned.

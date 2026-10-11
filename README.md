# 7Rail: EverRail in 7 Days to Die

A mashup mod. In 7 Days to Die you find a stalled EverRail locomotive on a railway that runs through your world,
jump-start it, drive it from EverRail's own cockpit, and use its wagons as a moving base.

Nothing from EverRail ships with the mod. A converter reads EverRail's models, textures, sounds, blueprints and
train rules from the player's own EverRail (Demo) install and writes a cache the 7 Days mod loads.

## What a player gets

- A railway on every world: a ring line with a spur into each biome and one to the first spawn, each ending in a
  balloon loop. It goes around POIs, cuts its own embankments and tunnels, and clears trees.
- EverRail's locomotive and wagons at 80% of EverRail size, built from EverRail's blueprints.
- Driving from EverRail's cockpit; the train keeps running when you leave the controls.
- EverRail's energy rules: it charges while it moves and drains all the time, with a countdown when it is falling.
  Gas, coal or oil jump-start a stalled locomotive.
- Wagon interiors as rooms you can build in with normal 7 Days blocks, with live windows onto the world outside.
- Add-ons: solar panels on the roof, and 7 Days' Generator Bank and Battery Bank built inside a wagon.
- A walkable roof with guard rails, and the Expedition Wagon's side door to step off when stopped.
- Zombies damage the train; aboard, you are sheltered from weather and biome hazards while it has energy.

Single player for now.

## Requirements

- 7 Days to Die V3.30 (b18), started without anti-cheat (`7DaysToDie.exe`), as any code mod needs.
- EverRail or the free EverRail Demo installed (Steam).

## Layout

| Folder | What it is |
|---|---|
| `sheets/` | The design, one JSON sheet per kind of thing. The source of truth: code and XML are generated from it. |
| `tools/` | `preflight.py` (checks every cell and reference), `gen_sheets.py`, `gen_xml.py`, `deploy.ps1`, `shot.ps1`. |
| `converter/SevenRailConvert/` | The converter (.NET 10, CUE4Parse). `converter/mappings/` holds the EverRail type mappings it needs. |
| `mod/` | The 7 Days mod (C#, .NET Framework 4.8, Harmony). |
| `native/ooz_ffi/` | A C wrapper over oozextract, kept as a fallback Oodle decoder (not used by default). |
| `NOTES.md` | Working notes: findings, decisions, what is left. |

## Building

The converter references CUE4Parse and vgmstream from a sibling `_tools` folder (`../_tools/CUE4Parse`,
`../_tools/vgmstream/bin`); see `NOTES.md` for the tool setup.

```
py -I tools/preflight.py --everrail-filelist <filelist> --sevendays "<7 Days folder>" --decomp <decompile folder>
powershell -File tools/deploy.ps1
```

`deploy.ps1` builds both projects, converts the local EverRail into the installed mod and copies the mod into
`7 Days To Die/Mods/7Rail`.

## Credits

- EverRail and 7 Days to Die belong to their makers; this mod ships none of their content.
- [CUE4Parse](https://github.com/FabianFG/CUE4Parse) (Apache-2.0) reads the Unreal files.
- [vgmstream](https://github.com/vgmstream/vgmstream) decodes EverRail's Bink audio.
- [oozextract](https://github.com/lvlvllvlvllvlvl/oozextract) (MIT).
- Mappings generated with [Dumper-7](https://github.com/Encryqed/Dumper-7).
- Built with Claude Code.

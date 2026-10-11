"""Preflight: overlay every design sheet and list what is unfinished or does not resolve.

usage: py -I tools/preflight.py [--everrail-filelist FILE] [--sevendays DIR] [--decomp DIR]

Exit code 0 only when every cell is filled and verified and every reference resolves.
"""
import argparse
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
SHEETS = ROOT / "sheets"
UNFILLED = re.compile(r"^(PENDING|MEASURE|TODO|\?)", re.I)

# column -> sheets whose ids it may reference (';'-separated lists allowed)
REFS = {
    ("everrail_assets", "used_by"): ["train_cars", "track", "train_events", "interiors", "@rules"],
    ("everrail_rules", "source_table"): ["everrail_assets"],
    ("train_cars", "meshes"): ["everrail_assets", "@none"],
    ("train_cars", "layout"): ["layouts", "@none"],
    ("train_cars", "power"): ["power_sources", "@none"],
    ("train_cars", "recipe"): ["recipes", "@none"],
    ("power_sources", "sevendays_item"): ["power_sources", "@item"],
    ("recipes", "car"): ["train_cars"],
    ("track", "mesh"): ["everrail_assets"],
    ("track", "bed_block"): ["@item"],
    ("layouts", "used_by"): ["interiors", "train_cars"],
    ("interiors", "layout"): ["layouts"],
    ("interiors", "ends"): ["layouts"],
    ("interiors", "car"): ["train_cars"],
    ("interiors", "shell_block"): ["@item"],
    ("train_events", "sounds"): ["everrail_assets"],
}


def load():
    sheets = {}
    for f in sorted(SHEETS.glob("*.json")):
        s = json.loads(f.read_text(encoding="utf-8"))
        sheets[s["sheet"]] = s
    return sheets


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--everrail-filelist")
    ap.add_argument("--sevendays")
    ap.add_argument("--decomp")
    a = ap.parse_args()

    sheets = load()
    ids = {name: {r["id"] for r in s["rows"]} for name, s in sheets.items()}
    problems = []

    items = set()
    if a.sevendays:
        cfg = pathlib.Path(a.sevendays) / "Data" / "Config"
        for xml in ("items.xml", "blocks.xml"):
            items |= set(re.findall(r'<(?:item|block) name="([^"]+)"', (cfg / xml).read_text(encoding="utf-8", errors="replace")))
        # items the mod itself adds (Config/items.xml in the mod)
        modcfg = ROOT / "mod" / "Config" / "items.xml"
        if modcfg.exists():
            items |= set(re.findall(r'<item name="([^"]+)"', modcfg.read_text(encoding="utf-8")))
    paks = None
    if a.everrail_filelist:
        paks = {l.strip().rsplit(".", 1)[0] for l in open(a.everrail_filelist, encoding="utf-8") if l.strip()}

    for name, s in sheets.items():
        cols = s["columns"]
        for r in s["rows"]:
            rid = r.get("id", "?")
            for c in cols:
                v = r.get(c)
                where = f"{name}.{rid}.{c}"
                if isinstance(v, str) and c in ("output_e_per_s", "consumes", "sevendays_use"):
                    for ref in re.findall(r"\brule_\w+", v):
                        if ref not in ids.get("everrail_rules", set()):
                            problems.append(f"BROKEN REF {where}: '{ref}' not in everrail_rules")
                if v is None or (isinstance(v, str) and (v.strip() == "" and c not in ("effect",))):
                    problems.append(f"UNFILLED   {where}")
                    continue
                if isinstance(v, str) and UNFILLED.match(v.strip()):
                    problems.append(f"UNVERIFIED {where} = {v}")
                    continue
                kinds = REFS.get((name, c))
                if kinds and isinstance(v, str):
                    for part in [p.strip() for p in v.split(";") if p.strip()]:
                        ok = any(part in ids.get(k, set()) for k in kinds if not k.startswith("@"))
                        ok = ok or ("@none" in kinds and part == "none") or ("@rules" in kinds and part == "rules")
                        if not ok and "@item" in kinds:
                            ok = (not items) or part in items
                            if not ok:
                                problems.append(f"NO ITEM    {where}: 7 Days has no item/block '{part}'")
                                continue
                        if not ok:
                            problems.append(f"BROKEN REF {where}: '{part}' not in {kinds}")
            for col in ("path", "blueprint"):
                if name in ("everrail_assets", "layouts") and col in r and paks is not None:
                    for part in r[col].split(";"):
                        if part not in paks:
                            problems.append(f"NOT IN PAK {name}.{rid}.{col}: {part}")
            if name == "recipes" and items:
                for ing in r["ingredients"].split(";"):
                    item = ing.split(":")[0]
                    if item not in items:
                        problems.append(f"NO ITEM    recipes.{rid}.ingredients: '{item}'")

    # every rule must be used, every asset must be used by something that exists
    if a.decomp:
        src = "\n".join(p.read_text(encoding="utf-8", errors="replace") for p in pathlib.Path(a.decomp).rglob("*.cs")
                        if p.name in {"World.cs", "ChunkCluster.cs", "ModEvents.cs", "EntityPlayerLocal.cs", "GameIO.cs",
                                      "PlayerActionsLocal.cs", "Inventory.cs", "GameManager.cs", "IModApi.cs"})
        for r in sheets["hooks"]["rows"]:
            for member in re.findall(r"\b([A-Z][A-Za-z]+)\b(?=\s*\()|\.([A-Za-z]+)\b", r["api"]):
                m = member[0] or member[1]
                if m in {"Mod", "Bounds", "List", "Entity", "EntityZombie", "Activate", "MoveForward", "MoveBack", "Jump", "PatchAll"}:
                    continue
                if m not in src:
                    problems.append(f"NO API     hooks.{r['id']}.api: '{m}' not found in decompile")

    for p in problems:
        print(p)
    print(f"\n{len(problems)} open cell(s) / reference(s) across {sum(len(s['rows']) for s in sheets.values())} rows in {len(sheets)} sheets")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())

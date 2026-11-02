#!/usr/bin/env python3
"""Turns AzerothCore world tables, as JSON files, into one spawn file per map tile.

The output is what the game reads: see docs/decisions/0007-creatures.md for the rows.
Neither the tables nor the output may be committed, so both default to ignored folders.

    python tools/spawn-exporter/export_spawns.py [--tables data/azerothcore] [--out spawns]
"""

import argparse
import json
import math
from collections import defaultdict
from pathlib import Path

TILE_SIZE = 1600.0 / 3.0
TILES_PER_SIDE = 64
MAP_ORIGIN = TILES_PER_SIDE / 2 * TILE_SIZE

# WoW's walking speed in yards per second, which creature_template.speed_walk multiplies.
BASE_WALK_SPEED = 2.5

# creature_template.flags_extra: invisible helpers that scripts use.
FLAG_TRIGGER = 0x80

# The normal world phase, which everyone sees without having done any quests.
PHASE_NORMAL = 1

# Internal dummies, which have names like "[DND] TAR Pedestal - General Goods".
DUMMY_NAME_PREFIX = "[DND]"

# Guids can differ between versions of the tables, so game_event_creature misses some event
# spawns. A creature with at least this share of its spawns in the table is treated as all event.
EVENT_ENTRY_SHARE = 0.5

# Holiday spawns stand on the regular spawns they replace, so whatever gets past the above
# goes if it's this close to an older spawn of another kind.
STACKED_YARDS = 1.0

# creature.equipment_id: use a random one of the creature's sets.
RANDOM_EQUIPMENT = -1

# creature_addon, which links a spawn to its path, says path_id, and AzerothCore's own
# convention for it is the spawn's guid times 10.
PATH_PER_GUID = 10


def tile_of(x, y):
    """The tile holding a WoW position, as WowCoords.TileAt has it: X runs east, Y south."""

    def index(value):
        return max(0, min(TILES_PER_SIDE - 1, math.floor((MAP_ORIGIN - value) / TILE_SIZE)))

    return index(y), index(x)


def choose(options, guid):
    """Picks the same option every run, so a creature keeps its look."""
    return options[guid % len(options)] if options else 0


def without_stacked(rows):
    """Keeps the oldest of spawns of different kinds in nearly the same place."""
    kept = {}
    for map_id, tile_x, tile_y, row in sorted(rows, key=lambda r: r[3]["guid"]):
        x, y, z = row["pos"]
        cell = (map_id, math.floor(x / STACKED_YARDS), math.floor(y / STACKED_YARDS))
        stacked = False
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for other in kept.get((cell[0], cell[1] + dx, cell[2] + dy), ()):
                    ox, oy, oz = other["pos"]
                    if (
                        other["entry"] != row["entry"]
                        and abs(ox - x) < STACKED_YARDS
                        and abs(oy - y) < STACKED_YARDS
                        and abs(oz - z) < STACKED_YARDS
                    ):
                        stacked = True
        if not stacked:
            kept.setdefault(cell, []).append(row)
            yield map_id, tile_x, tile_y, row


def build_rows(creatures, templates, waypoints, equipment, items, event_guids=None, path_of_guid=None):
    """Yields (map, tile x, tile y, row) for each creature worth showing.

    event_guids comes from game_event_creature, and path_of_guid from creature_addon, whose
    missing rows fall back on AzerothCore's convention for path ids.
    """
    return without_stacked(_build_rows(creatures, templates, waypoints, equipment, items, event_guids or set(), path_of_guid or {}))


def event_entries(creatures, event_guids):
    """Creatures that are mostly event spawns."""
    spawns = defaultdict(int)
    events = defaultdict(int)
    for creature in creatures:
        spawns[creature["id1"]] += 1
        events[creature["id1"]] += creature["guid"] in event_guids
    return {entry for entry, count in events.items() if count >= spawns[entry] * EVENT_ENTRY_SHARE and count > 0}


def _build_rows(creatures, templates, waypoints, equipment, items, event_guids, path_of_guid):
    templates = {t["entry"]: t for t in templates}
    event_only = event_entries(creatures, event_guids)
    display_of_item = {i["entry"]: i["displayid"] for i in items}

    paths = defaultdict(list)
    for point in sorted(waypoints, key=lambda p: (p["id"], p["point"])):
        paths[point["id"]].append(
            {
                "x": point["position_x"],
                "y": point["position_y"],
                "z": point["position_z"],
                "delayMs": point["delay"] or 0,
            }
        )

    sets = defaultdict(dict)
    for row in equipment:
        sets[row["CreatureID"]][row["ID"]] = (row["ItemID1"], row["ItemID2"])

    for creature in creatures:
        guid = creature["guid"]
        if not creature["phaseMask"] & PHASE_NORMAL or guid in event_guids or creature["id1"] in event_only:
            continue

        entries = [creature[k] for k in ("id1", "id2", "id3") if creature[k]]
        entry = choose(entries, guid)
        template = templates.get(entry)
        if (
            template is None
            or template.get("flags_extra", 0) & FLAG_TRIGGER
            or template["name"].startswith(DUMMY_NAME_PREFIX)
        ):
            continue

        models = [template[f"modelid{i}"] for i in range(1, 5) if template[f"modelid{i}"]]
        display = choose(models, guid)
        if not display:
            continue

        x, y = creature["position_x"], creature["position_y"]
        row = {
            "guid": guid,
            "entry": entry,
            "name": template["name"],
            "displayId": display,
            "pos": [x, y, creature["position_z"]],
            "orientation": creature["orientation"],
            "wanderRadius": creature["wander_distance"],
            "movementType": creature["MovementType"],
            "scale": template["scale"] or 1.0,
            "walkSpeed": (template["speed_walk"] or 1.0) * BASE_WALK_SPEED,
        }

        path = paths.get(path_of_guid.get(guid, guid * PATH_PER_GUID))
        if creature["MovementType"] == 2 and path:
            row["waypoints"] = path

        own_sets = sets.get(entry, {})
        chosen = creature["equipment_id"]
        if chosen == RANDOM_EQUIPMENT and own_sets:
            chosen = choose(sorted(own_sets), guid)

        hands = own_sets.get(chosen)
        if hands:
            displays = [display_of_item.get(item, 0) for item in hands]
            if any(displays):
                row["weapons"] = displays

        tile_x, tile_y = tile_of(x, y)
        yield creature["map"], tile_x, tile_y, row


def load(folder, name):
    with open(folder / f"{name}.json", encoding="utf-8") as file:
        return json.load(file)


def load_if_present(folder, name):
    return load(folder, name) if (folder / f"{name}.json").exists() else []


def main():
    root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tables", type=Path, default=root / "data" / "azerothcore")
    parser.add_argument("--out", type=Path, default=root / "spawns")
    arguments = parser.parse_args()

    rows = defaultdict(list)
    for map_id, tile_x, tile_y, row in build_rows(
        load(arguments.tables, "creature"),
        load(arguments.tables, "creature_template"),
        load(arguments.tables, "waypoint_data"),
        load(arguments.tables, "creature_equip_template"),
        load(arguments.tables, "item_template"),
        # A negative event means a regular spawn that hides during it, so those stay.
        {row["guid"] for row in load_if_present(arguments.tables, "game_event_creature") if row["eventEntry"] > 0},
        {row["guid"]: row["path_id"] for row in load_if_present(arguments.tables, "creature_addon") if row.get("path_id")},
    ):
        rows[map_id, tile_x, tile_y].append(row)

    for (map_id, tile_x, tile_y), tile_rows in rows.items():
        folder = arguments.out / str(map_id)
        folder.mkdir(parents=True, exist_ok=True)
        with open(folder / f"{tile_x}_{tile_y}.json", "w", encoding="utf-8") as file:
            json.dump(tile_rows, file, ensure_ascii=False, separators=(",", ":"))

    print(f"Wrote {sum(len(r) for r in rows.values())} creatures to {len(rows)} tiles in {arguments.out}")


if __name__ == "__main__":
    main()

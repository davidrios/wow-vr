"""Run with: python -m unittest discover tools/spawn-exporter. The rows here are made up."""

import unittest

from export_spawns import build_object_rows, build_rows, tile_of


def creature(**changes):
    row = {
        "guid": 7,
        "id1": 1,
        "id2": 0,
        "id3": 0,
        "map": 0,
        "phaseMask": 1,
        "position_x": -9450.0,
        "position_y": 60.0,
        "position_z": 56.0,
        "orientation": 3.0,
        "wander_distance": 0.0,
        "MovementType": 0,
        "equipment_id": 1,
    }
    return row | changes


def template(**changes):
    row = {
        "entry": 1,
        "name": "Made Up",
        "modelid1": 100,
        "modelid2": 0,
        "modelid3": 0,
        "modelid4": 0,
        "scale": 1.5,
        "speed_walk": 2.0,
        "flags_extra": 0,
    }
    return row | changes


def rows(creatures, templates=None, waypoints=(), equipment=(), items=()):
    return list(build_rows(creatures, templates or [template()], list(waypoints), list(equipment), list(items)))


class TileTests(unittest.TestCase):
    def test_goldshire_is_on_its_tile(self):
        self.assertEqual((31, 49), tile_of(-9450.0, 60.0))


class RowTests(unittest.TestCase):
    def test_fills_in_a_row(self):
        map_id, tile_x, tile_y, row = rows([creature()])[0]
        self.assertEqual((0, 31, 49), (map_id, tile_x, tile_y))
        self.assertEqual("Made Up", row["name"])
        self.assertEqual(100, row["displayId"])
        self.assertEqual(1.5, row["scale"])
        self.assertEqual(5.0, row["walkSpeed"])

    def test_skips_triggers_and_other_phases_and_unknowns(self):
        self.assertEqual([], rows([creature()], [template(flags_extra=0x80)]))
        self.assertEqual([], rows([creature(phaseMask=2)]))
        self.assertEqual([], rows([creature(id1=99)]))

    def test_skips_dummies(self):
        self.assertEqual([], rows([creature()], [template(name="[DND] TAR Pedestal")]))

    def test_a_spawn_stacked_on_an_older_one_of_another_kind_goes(self):
        table = [template(), template(entry=2, name="Wild Turkey")]
        kept = rows([creature(guid=1), creature(guid=2, id1=2, position_x=-9450.5)], table)
        self.assertEqual(["Made Up"], [r[3]["name"] for r in kept])

    def test_stacks_of_one_kind_stay(self):
        kept = rows([creature(guid=1), creature(guid=2, position_x=-9450.5)])
        self.assertEqual(2, len(kept))

    def test_event_spawns_go(self):
        spawns = [creature(guid=1), creature(guid=2, position_x=0.0), creature(guid=3, position_x=500.0)]
        kept = list(build_rows(spawns, [template()], [], [], [], event_guids={3}))
        self.assertEqual([1, 2], [r[3]["guid"] for r in kept])

    def test_a_creature_mostly_in_events_goes_entirely(self):
        table = [template(entry=2, name="Wild Turkey")]
        spawns = [creature(guid=1, id1=2), creature(guid=2, id1=2, position_x=0.0)]
        self.assertEqual([], list(build_rows(spawns, table, [], [], [], event_guids={1})))

    def test_the_addon_table_replaces_the_path_convention(self):
        points = [{"id": 5, "point": 1, "position_x": 1.0, "position_y": 0.0, "position_z": 0.0, "delay": 0}]
        kept = list(build_rows([creature(MovementType=2)], [template()], points, [], [], path_of_guid={7: 5}))
        self.assertEqual(1, len(kept[0][3]["waypoints"]))

    def test_the_same_guid_keeps_its_look(self):
        table = [template(modelid2=200)]
        self.assertEqual(rows([creature()], table), rows([creature()], table))

    def test_paths_come_from_ten_times_the_guid_in_order(self):
        points = [
            {"id": 70, "point": 2, "position_x": 2.0, "position_y": 0.0, "position_z": 0.0, "delay": None},
            {"id": 70, "point": 1, "position_x": 1.0, "position_y": 0.0, "position_z": 0.0, "delay": 500},
        ]
        row = rows([creature(MovementType=2)], waypoints=points)[0][3]
        self.assertEqual([1.0, 2.0], [p["x"] for p in row["waypoints"]])
        self.assertEqual([500, 0], [p["delayMs"] for p in row["waypoints"]])

    def test_weapons_become_item_displays(self):
        sets = [{"CreatureID": 1, "ID": 1, "ItemID1": 10, "ItemID2": 0}]
        row = rows([creature()], equipment=sets, items=[{"entry": 10, "displayid": 555}])[0][3]
        self.assertEqual([555, 0], row["weapons"])


class ObjectRowsTest(unittest.TestCase):
    TEMPLATES = [
        {"entry": 1, "type": 19, "displayId": 7, "name": "Mailbox", "size": 1.5},
        {"entry": 2, "type": 3, "displayId": 8, "name": "Peacebloom", "size": 1},
        {"entry": 3, "type": 5, "displayId": 0, "name": "Nothing to see", "size": 1},
        {"entry": 4, "type": 5, "displayId": 9, "name": "Holiday Lantern", "size": 1},
        {"entry": 5, "type": 5, "displayId": 10, "name": "Darkmoon Faire Banner", "size": 1},
    ]

    @staticmethod
    def spawn(guid, entry, phase=1):
        return {
            "guid": guid, "id": entry, "map": 0, "phaseMask": phase, "position_x": -9456.0, "position_y": 46.0, "position_z": 56.0,
            "orientation": 1.0, "rotation0": 0.0, "rotation1": 0.0, "rotation2": 0.5, "rotation3": 0.8,
        }

    def rows(self, objects, events=None):
        return [r[3] for r in build_object_rows(objects, self.TEMPLATES, events)]

    def test_a_kept_object_has_its_look_and_rotation(self):
        row = self.rows([self.spawn(10, 1)])[0]
        self.assertEqual((row["name"], row["displayId"], row["scale"]), ("Mailbox", 7, 1.5))
        self.assertEqual(row["rotation"], [0.0, 0.0, 0.5, 0.8])

    def test_nodes_invisible_holiday_and_other_phase_objects_go(self):
        objects = [self.spawn(1, 2), self.spawn(2, 3), self.spawn(3, 5), self.spawn(4, 1, phase=2), self.spawn(5, 99)]
        self.assertEqual(self.rows(objects), [])

    def test_an_object_mostly_in_events_goes_entirely(self):
        objects = [self.spawn(1, 4), self.spawn(2, 4), self.spawn(3, 1)]
        self.assertEqual([r["guid"] for r in self.rows(objects, {1})], [3])


if __name__ == "__main__":
    unittest.main()

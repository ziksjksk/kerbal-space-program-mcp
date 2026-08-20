import unittest

from server.craft_model import CraftValidationError, normalise_part_spec, validate_craft_document


class CraftModelTests(unittest.TestCase):
    def test_normalises_defaults(self) -> None:
        part = normalise_part_spec({"part": "mk1pod.v2"}, 0)
        self.assertEqual(part["id"], "part-1")
        self.assertEqual(part["position"], [0.0, 0.0, 0.0])
        self.assertEqual(part["rotation"], [0.0, 0.0, 0.0, 1.0])

    def test_rejects_duplicate_ids(self) -> None:
        with self.assertRaisesRegex(CraftValidationError, "duplicate part id"):
            validate_craft_document(
                {
                    "parts": [
                        {"id": "same", "part": "mk1pod.v2"},
                        {"id": "same", "part": "fuelTankSmall"},
                    ]
                }
            )

    def test_rejects_parent_cycle(self) -> None:
        with self.assertRaisesRegex(CraftValidationError, "parent cycle"):
            validate_craft_document(
                {
                    "parts": [
                        {"id": "a", "part": "mk1pod.v2", "parent_id": "b"},
                        {"id": "b", "part": "fuelTankSmall", "parent_id": "a"},
                    ]
                }
            )

    def test_connected_craft_has_one_root(self) -> None:
        with self.assertRaisesRegex(CraftValidationError, "exactly one root"):
            validate_craft_document(
                {
                    "parts": [
                        {"id": "a", "part": "mk1pod.v2"},
                        {"id": "b", "part": "fuelTankSmall"},
                    ]
                },
                require_connected=True,
            )

    def test_accepts_out_of_order_tree(self) -> None:
        craft = validate_craft_document(
            {
                "name": "out of order",
                "parts": [
                    {"id": "child", "part": "fuelTankSmall", "parent_id": "root"},
                    {"id": "root", "part": "mk1pod.v2"},
                ]
            },
            require_connected=True,
        )
        self.assertEqual(craft["parts"][0]["parent_id"], "root")

    def test_preserves_node_snap_option(self) -> None:
        part = normalise_part_spec({"part": "fuelTankSmall", "snap_to_node": False})
        self.assertFalse(part["snap_to_node"])


if __name__ == "__main__":
    unittest.main()


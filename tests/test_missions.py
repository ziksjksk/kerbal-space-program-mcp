import unittest

from server.craft_model import validate_craft_document
from server.missions import build_space_station, plan_moon_landing


class MissionHelperTests(unittest.TestCase):
    def setUp(self):
        self.bodies = {
            "bodies": [
                {
                    "name": "Kerbin",
                    "radius_m": 600000.0,
                    "grav_parameter_m3_s2": 3.5316e12,
                    "reference_body": "Sun",
                    "orbit": {
                        "semi_major_axis_m": 13599840256.0,
                        "mean_anomaly_at_epoch_rad": 0.0,
                        "mean_motion_rad_s": 1.0e-4,
                        "epoch_ut": 0.0,
                    },
                },
                {
                    "name": "Mun",
                    "radius_m": 200000.0,
                    "grav_parameter_m3_s2": 6.5138398e10,
                    "reference_body": "Kerbin",
                    "sphere_of_influence_m": 2429559.0,
                    "orbit": {
                        "semi_major_axis_m": 12000000.0,
                        "mean_anomaly_at_epoch_rad": 0.2,
                        "mean_motion_rad_s": 2.0e-5,
                        "epoch_ut": 0.0,
                    },
                },
            ]
        }

    def test_station_template_is_one_connected_root(self):
        craft = build_space_station()
        result = validate_craft_document(craft, require_connected=True)
        self.assertEqual(result["name"], "MCP Orbital Station Core")
        self.assertEqual(len(result["parts"]), 10)
        self.assertEqual(sum(part["parent_id"] is None for part in result["parts"]), 1)

    def test_moon_plan_calculates_body_to_moon_transfer(self):
        result = plan_moon_landing(
            bodies_payload=self.bodies,
            flight_payload={"body": "Kerbin", "situation": "ORBITING", "universal_time": 1000.0},
            landing_latitude=12.5,
            landing_longitude=-34.0,
        )
        self.assertEqual(result["target_body"], "Mun")
        self.assertTrue(result["transfer_required"])
        self.assertTrue(result["transfer_supported"])
        self.assertGreater(result["transfer"]["total_estimated_delta_v_mps"], 0.0)
        self.assertEqual(result["landing_target"]["latitude_deg"], 12.5)

    def test_moon_plan_marks_descent_ready_on_target_body(self):
        result = plan_moon_landing(
            bodies_payload=self.bodies,
            flight_payload={"body": "Mun", "situation": "ORBITING", "universal_time": 1000.0},
        )
        self.assertTrue(result["ready_for_soft_landing"])
        self.assertFalse(result["transfer_required"])
        self.assertIsNone(result["transfer"])


if __name__ == "__main__":
    unittest.main()


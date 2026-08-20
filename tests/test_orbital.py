import unittest

from server.orbital import plan_circular_hohmann_transfer


class OrbitalPlanTests(unittest.TestCase):
    def test_phase_alignment_uses_loaded_epoch_data_when_available(self):
        bodies = {
            "bodies": [
                {
                    "name": "Kerbin",
                    "reference_body": "Kerbol",
                    "radius_m": 600000,
                    "grav_parameter_m3_s2": 3.5316e12,
                    "orbit": {
                        "semi_major_axis_m": 13599840256,
                        "mean_anomaly_at_epoch_rad": 0.0,
                        "mean_motion_rad_s": 1.0e-7,
                        "epoch_ut": 0.0,
                        "longitude_of_ascending_node_deg": 0.0,
                        "argument_of_periapsis_deg": 0.0,
                    },
                },
                {
                    "name": "Duna",
                    "reference_body": "Kerbol",
                    "radius_m": 320000,
                    "grav_parameter_m3_s2": 3.0136e11,
                    "orbit": {
                        "semi_major_axis_m": 20726155264,
                        "mean_anomaly_at_epoch_rad": 0.5,
                        "mean_motion_rad_s": 5.0e-8,
                        "epoch_ut": 0.0,
                        "longitude_of_ascending_node_deg": 0.0,
                        "argument_of_periapsis_deg": 0.0,
                    },
                },
                {
                    "name": "Kerbol",
                    "radius_m": 261600000,
                    "grav_parameter_m3_s2": 1.1723e18,
                },
            ]
        }
        result = plan_circular_hohmann_transfer(
            bodies_payload=bodies,
            flight_payload={"body": "Kerbin", "universal_time": 1000000.0},
            destination_body="Duna",
        )
        alignment = result["phase_alignment"]
        self.assertTrue(alignment["verified_from_loaded_orbit"])
        self.assertIsInstance(alignment["current_target_lead_deg"], float)
        self.assertIsInstance(alignment["phase_error_deg"], float)


if __name__ == "__main__":
    unittest.main()


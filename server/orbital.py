"""Small, explicit patched-conic transfer estimates for KSP MCP.

The game plugin remains the source of truth for celestial-body parameters and
native maneuver nodes. This module only performs a deterministic circular
Hohmann estimate from the data returned by flight.bodies. Keeping the math
outside Unity makes it easy to test and lets an MCP client inspect the
assumptions before it creates a node or burns an engine.
"""

from __future__ import annotations

import math
from typing import Any


class OrbitalPlanError(ValueError):
    """Raised when the running game's body data cannot support this estimate."""


def _finite_number(value: Any, field: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(float(value)):
        raise OrbitalPlanError(f"{field} must be a finite number")
    return float(value)


def _positive(value: Any, field: str) -> float:
    number = _finite_number(value, field)
    if number <= 0:
        raise OrbitalPlanError(f"{field} must be greater than zero")
    return number


def _body_name(body: dict[str, Any]) -> str:
    return str(body.get("name") or body.get("display_name") or "")


def _find_body(bodies: list[dict[str, Any]], name: str, field: str) -> dict[str, Any]:
    wanted = name.strip().casefold()
    if not wanted:
        raise OrbitalPlanError(f"{field} must be a non-empty body name")
    for body in bodies:
        names = {
            str(body.get("name") or "").casefold(),
            str(body.get("display_name") or "").casefold(),
        }
        if wanted in names:
            return body
    available = ", ".join(sorted(filter(None, (_body_name(body) for body in bodies))))
    raise OrbitalPlanError(f"{field} {name!r} was not found in the running KSP body list; available: {available}")


def _orbit_number(body: dict[str, Any], key: str, field: str) -> float:
    orbit = body.get("orbit")
    if not isinstance(orbit, dict):
        raise OrbitalPlanError(f"{field} has no heliocentric orbit in the running game's data")
    return _positive(orbit.get(key), f"{field}.orbit.{key}")


def _normalise_degrees(angle: float) -> float:
    result = (angle + 180.0) % 360.0 - 180.0
    return 180.0 if math.isclose(result, -180.0, abs_tol=1e-12) else result


def _current_orbital_longitude_deg(body: dict[str, Any], universal_time: float) -> float | None:
    """Estimate a body's current inertial longitude from KSP orbit fields.

    KSP provides the mean anomaly at epoch and mean motion for loaded body
    orbits. This is intentionally optional: older/modified installations may
    omit one of the fields, in which case the transfer plan remains a useful
    delta-v estimate but cannot claim that the departure window is aligned.
    """

    orbit = body.get("orbit")
    if not isinstance(orbit, dict):
        return None
    values: list[float] = []
    for key in ("mean_anomaly_at_epoch_rad", "mean_motion_rad_s", "epoch_ut"):
        value = orbit.get(key)
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(float(value)):
            return None
        values.append(float(value))
    mean_anomaly = values[0] + values[1] * (universal_time - values[2])
    lan = orbit.get("longitude_of_ascending_node_deg", 0.0)
    argument = orbit.get("argument_of_periapsis_deg", 0.0)
    if any(isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(float(value)) for value in (lan, argument)):
        return None
    return _normalise_degrees(math.degrees(mean_anomaly) + float(lan) + float(argument))


def _sqrt(value: float, field: str) -> float:
    if value <= 0 or not math.isfinite(value):
        raise OrbitalPlanError(f"could not calculate a positive {field}")
    return math.sqrt(value)


def plan_circular_hohmann_transfer(
    *,
    bodies_payload: dict[str, Any],
    flight_payload: dict[str, Any],
    destination_body: str,
    origin_body: str | None = None,
    parking_altitude_m: float = 80_000.0,
    target_altitude_m: float = 80_000.0,
    direction: str = "prograde",
) -> dict[str, Any]:
    """Return a readable interplanetary Hohmann estimate.

    origin_body and destination_body must orbit the same reference body. Their
    heliocentric orbits are treated as circular and coplanar; the result is
    therefore a planning aid, not an autopilot guarantee.
    """

    if not isinstance(bodies_payload, dict) or not isinstance(bodies_payload.get("bodies"), list):
        raise OrbitalPlanError("flight.bodies returned no usable body list")
    if not isinstance(flight_payload, dict):
        raise OrbitalPlanError("flight.state returned no usable vessel state")

    bodies = [body for body in bodies_payload["bodies"] if isinstance(body, dict)]
    destination = _find_body(bodies, destination_body, "destination_body")
    selected_origin = origin_body or flight_payload.get("body")
    if not isinstance(selected_origin, str) or not selected_origin.strip():
        raise OrbitalPlanError("origin_body is required when no active vessel body is available")
    origin = _find_body(bodies, selected_origin, "origin_body")

    origin_name = _body_name(origin)
    destination_name = _body_name(destination)
    if origin_name.casefold() == destination_name.casefold():
        raise OrbitalPlanError("origin_body and destination_body must be different")

    if direction.strip().casefold() != "prograde":
        raise OrbitalPlanError("this first transfer planner supports only prograde coplanar Hohmann transfers")

    origin_reference = origin.get("reference_body")
    destination_reference = destination.get("reference_body")
    if not isinstance(origin_reference, str) or not isinstance(destination_reference, str):
        raise OrbitalPlanError("both bodies must have a common reference body for an interplanetary estimate")
    if origin_reference.casefold() != destination_reference.casefold():
        raise OrbitalPlanError(
            "origin and destination do not share a reference body; use a moon/planet patched-conic plan instead"
        )
    central = _find_body(bodies, origin_reference, "reference_body")

    central_mu = _positive(central.get("grav_parameter_m3_s2"), "reference_body.grav_parameter_m3_s2")
    origin_mu = _positive(origin.get("grav_parameter_m3_s2"), "origin_body.grav_parameter_m3_s2")
    destination_mu = _positive(destination.get("grav_parameter_m3_s2"), "destination_body.grav_parameter_m3_s2")
    origin_radius = _positive(origin.get("radius_m"), "origin_body.radius_m")
    destination_radius = _positive(destination.get("radius_m"), "destination_body.radius_m")
    parking_altitude = _finite_number(parking_altitude_m, "parking_altitude_m")
    target_altitude = _finite_number(target_altitude_m, "target_altitude_m")
    if parking_altitude < 0 or target_altitude < 0:
        raise OrbitalPlanError("parking_altitude_m and target_altitude_m cannot be negative")

    origin_orbit_radius = _orbit_number(origin, "semi_major_axis_m", "origin_body")
    destination_orbit_radius = _orbit_number(destination, "semi_major_axis_m", "destination_body")
    transfer_semi_major = (origin_orbit_radius + destination_orbit_radius) / 2.0

    origin_circular_speed = _sqrt(central_mu / origin_orbit_radius, "origin circular heliocentric speed")
    destination_circular_speed = _sqrt(central_mu / destination_orbit_radius, "destination circular heliocentric speed")
    transfer_departure_speed = _sqrt(
        central_mu * (2.0 / origin_orbit_radius - 1.0 / transfer_semi_major),
        "transfer departure speed",
    )
    transfer_arrival_speed = _sqrt(
        central_mu * (2.0 / destination_orbit_radius - 1.0 / transfer_semi_major),
        "transfer arrival speed",
    )

    heliocentric_departure_dv = transfer_departure_speed - origin_circular_speed
    heliocentric_arrival_dv = destination_circular_speed - transfer_arrival_speed
    departure_v_inf = abs(heliocentric_departure_dv)
    arrival_v_inf = abs(heliocentric_arrival_dv)

    parking_radius = origin_radius + parking_altitude
    target_radius = destination_radius + target_altitude
    parking_speed = _sqrt(origin_mu / parking_radius, "origin parking-orbit speed")
    departure_escape_speed = _sqrt(
        departure_v_inf * departure_v_inf + 2.0 * origin_mu / parking_radius,
        "origin hyperbolic departure speed",
    )
    target_circular_speed = _sqrt(destination_mu / target_radius, "target parking-orbit speed")
    target_capture_speed = _sqrt(
        arrival_v_inf * arrival_v_inf + 2.0 * destination_mu / target_radius,
        "target hyperbolic arrival speed",
    )
    departure_dv = abs(departure_escape_speed - parking_speed)
    arrival_dv = abs(target_capture_speed - target_circular_speed)

    transfer_time = math.pi * _sqrt(transfer_semi_major**3 / central_mu, "Hohmann transfer time")
    destination_mean_motion = _sqrt(central_mu / destination_orbit_radius**3, "target mean motion")
    phase_angle = _normalise_degrees(math.degrees(math.pi - destination_mean_motion * transfer_time))

    warnings: list[str] = []
    for body, altitude, label in (
        (origin, parking_altitude, "origin parking orbit"),
        (destination, target_altitude, "destination parking orbit"),
    ):
        atmosphere = body.get("atmosphere") is True
        max_atmosphere = body.get("max_atmosphere_altitude_m")
        if atmosphere and isinstance(max_atmosphere, (int, float)) and altitude < float(max_atmosphere):
            warnings.append(
                f"{label} is inside the reported atmosphere ({float(max_atmosphere):.0f} m); "
                "raise the altitude before circularising"
            )
    if origin.get("ocean") is True and parking_altitude < 1_000.0:
        warnings.append("origin parking altitude is close to the surface of an ocean world")
    if destination.get("ocean") is True and target_altitude < 1_000.0:
        warnings.append("destination parking altitude is close to the surface of an ocean world")

    current_ut = flight_payload.get("universal_time")
    current_ut = float(current_ut) if isinstance(current_ut, (int, float)) else None
    current_phase = None
    phase_error = None
    if current_ut is not None:
        origin_longitude = _current_orbital_longitude_deg(origin, current_ut)
        destination_longitude = _current_orbital_longitude_deg(destination, current_ut)
        if origin_longitude is not None and destination_longitude is not None:
            current_phase = _normalise_degrees(destination_longitude - origin_longitude)
            phase_error = _normalise_degrees(current_phase - phase_angle)
        else:
            warnings.append(
                "the running game did not expose enough orbital epoch data to verify the current departure phase angle"
            )
    return {
        "model": "circular_coplanar_hohmann_patched_conic_estimate",
        "origin_body": origin_name,
        "destination_body": destination_name,
        "reference_body": _body_name(central),
        "direction": "prograde",
        "phase_angle_at_departure_deg": phase_angle,
        "transfer_time_s": transfer_time,
        "transfer_time_days": transfer_time / 86_400.0,
        "current_universal_time": current_ut,
        "geometry": {
            "origin_orbit_radius_m": origin_orbit_radius,
            "destination_orbit_radius_m": destination_orbit_radius,
            "transfer_semi_major_axis_m": transfer_semi_major,
            "origin_parking_radius_m": parking_radius,
            "destination_parking_radius_m": target_radius,
        },
        "burns": {
            "departure_from_origin_parking_orbit": {
                "delta_v_mps": departure_dv,
                "prograde_mps": math.copysign(departure_dv, heliocentric_departure_dv or 1.0),
                "v_infinity_mps": departure_v_inf,
                "heliocentric_delta_v_mps": heliocentric_departure_dv,
                "description": "raise/lower heliocentric energy to enter the transfer ellipse",
            },
            "arrival_capture_at_destination": {
                "delta_v_mps": arrival_dv,
                "prograde_mps": math.copysign(arrival_dv, heliocentric_arrival_dv or 1.0),
                "v_infinity_mps": arrival_v_inf,
                "heliocentric_delta_v_mps": heliocentric_arrival_dv,
                "description": "circularise into the requested destination parking orbit",
            },
        },
        "total_estimated_delta_v_mps": departure_dv + arrival_dv,
        "phase_alignment": {
            "required_target_lead_deg": phase_angle,
            "current_target_lead_deg": current_phase,
            "phase_error_deg": phase_error,
            "verified_from_loaded_orbit": current_phase is not None,
            "how_to_use": "compare the current target-body phase to this value before committing the departure burn",
        },
        "assumptions": [
            "origin and destination heliocentric orbits are circular and coplanar",
            "burns are instantaneous and aligned with local prograde/retrograde",
            "patched-conic sphere-of-influence transitions are approximated",
            "no plane change, launch losses, drag, steering losses, boiloff, or finite-burn effects",
            "parking and target orbit altitudes are measured above the reported body radius",
        ],
        "warnings": warnings,
        "native_node_note": (
            "ksp_flight_add_maneuver_node can create a native KSP node after the user confirms the UT and burn. "
            "This planner does not create or execute a node automatically."
        ),
    }

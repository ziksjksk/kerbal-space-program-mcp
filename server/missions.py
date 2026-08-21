Import-Clixml: Unexpected end of file has occurred. The following elements are not closed: S, En, DCT, Obj, En, DCT, Obj, En, DCT,
Obj, Objs. Line 123, position 38.
"""Small mission helpers built on top of the live KSP bridge.

The plugin remains the source of truth for loaded parts, vessel state, and
the actual flight controller.  This module only builds deterministic plans
and stock-editor documents so a no-visual MCP client can make one explicit
call instead of reconstructing common mission boilerplate.
"""

from __future__ import annotations

import math
from typing import Any


class MissionPlanError(ValueError):
    """Raised when live KSP data cannot support a transparent mission plan."""


def _number(value: Any, field: str, *, positive: bool = False) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(float(value)):
        raise MissionPlanError(f"{field} must be a finite number")
    result = float(value)
    if positive and result <= 0:
        raise MissionPlanError(f"{field} must be greater than zero")
    return result


def _body_name(body: dict[str, Any]) -> str:
    return str(body.get("name") or body.get("display_name") or "")


def _find_body(bodies: list[dict[str, Any]], name: str, field: str) -> dict[str, Any]:
    wanted = str(name or "").strip().casefold()
    if not wanted:
        raise MissionPlanError(f"{field} must be a non-empty body name")
    for body in bodies:
        if wanted in {
            str(body.get("name") or "").casefold(),
            str(body.get("display_name") or "").casefold(),
        }:
            return body
    available = ", ".join(sorted(filter(None, (_body_name(item) for item in bodies))))
    raise MissionPlanError(f"{field} {name!r} was not found in the live body list; available: {available}")


def _sqrt(value: float, field: str) -> float:
    if value <= 0 or not math.isfinite(value):
        raise MissionPlanError(f"could not calculate a positive {field}")
    return math.sqrt(value)


def _normalise_degrees(angle: float) -> float:
    result = (angle + 180.0) % 360.0 - 180.0
    return 180.0 if math.isclose(result, -180.0, abs_tol=1e-12) else result


def _body_longitude(body: dict[str, Any], universal_time: float | None) -> float | None:
    if universal_time is None or not isinstance(body.get("orbit"), dict):
        return None
    orbit = body["orbit"]
    keys = ("mean_anomaly_at_epoch_rad", "mean_motion_rad_s", "epoch_ut")
    if any(isinstance(orbit.get(key), bool) or not isinstance(orbit.get(key), (int, float)) for key in keys):
        return None
    mean_anomaly = float(orbit["mean_anomaly_at_epoch_rad"]) + float(orbit["mean_motion_rad_s"]) * (
        universal_time - float(orbit["epoch_ut"])
    )
    lan = orbit.get("longitude_of_ascending_node_deg", 0.0)
    argument = orbit.get("argument_of_periapsis_deg", 0.0)
    if any(isinstance(value, bool) or not isinstance(value, (int, float)) for value in (lan, argument)):
        return None
    return _normalise_degrees(math.degrees(mean_anomaly) + float(lan) + float(argument))


def plan_moon_landing(
    *,
    bodies_payload: dict[str, Any],
    flight_payload: dict[str, Any],
    target_body: str = "Mun",
    parking_altitude_m: float = 80_000.0,
    target_altitude_m: float = 15_000.0,
    landing_latitude: float = 0.0,
    landing_longitude: float = 0.0,
) -> dict[str, Any]:
    """Return a live-data lunar transfer and soft-landing handoff plan.

    When the active vessel is already at the target body this reports the
    powered-descent entry conditions.  When it is orbiting the target body
    (the usual Kerbin -> Mun case), it adds a simple patched-conic transfer
    estimate from the current body's parking orbit.  It deliberately does
    not create a maneuver node or burn an engine.
    """

    if not isinstance(bodies_payload, dict) or not isinstance(bodies_payload.get("bodies"), list):
        raise MissionPlanError("flight.bodies returned no usable body list")
    if not isinstance(flight_payload, dict):
        raise MissionPlanError("flight.state returned no usable vessel state")

    bodies = [body for body in bodies_payload["bodies"] if isinstance(body, dict)]
    target = _find_body(bodies, target_body, "target_body")
    active_name = flight_payload.get("body")
    if not isinstance(active_name, str) or not active_name.strip():
        raise MissionPlanError("the active vessel has no current celestial body")
    origin = _find_body(bodies, active_name, "active_body")
    target_name = _body_name(target)
    origin_name = _body_name(origin)
    parking_altitude = _number(parking_altitude_m, "parking_altitude_m")
    target_altitude = _number(target_altitude_m, "target_altitude_m")
    if parking_altitude < 0 or target_altitude < 0:
        raise MissionPlanError("parking_altitude_m and target_altitude_m cannot be negative")
    if not -90.0 <= float(landing_latitude) <= 90.0:
        raise MissionPlanError("landing_latitude must be -90..90")
    if not -180.0 <= float(landing_longitude) <= 180.0:
        raise MissionPlanError("landing_longitude must be -180..180")

    current_ut = flight_payload.get("universal_time")
    current_ut = float(current_ut) if isinstance(current_ut, (int, float)) and not isinstance(current_ut, bool) else None
    same_body = origin_name.casefold() == target_name.casefold()
    warnings: list[str] = [
        "the transfer is a patched-conic estimate; plane changes, finite burns, steering loss, and terrain are not solved",
        "the landing controller must remain observable through ksp_realtime_state and can be stopped for human takeover",
    ]

    transfer: dict[str, Any] | None = None
    transfer_supported = False
    if not same_body:
        target_reference = target.get("reference_body")
        if isinstance(target_reference, str) and target_reference.casefold() == origin_name.casefold():
            orbit = target.get("orbit")
            target_orbit_radius = orbit.get("semi_major_axis_m") if isinstance(orbit, dict) else None
            target_orbit_radius = _number(target_orbit_radius, "target_body.orbit.semi_major_axis_m", positive=True)
            origin_mu = _number(origin.get("grav_parameter_m3_s2"), "active_body.grav_parameter_m3_s2", positive=True)
            target_mu = _number(target.get("grav_parameter_m3_s2"), "target_body.grav_parameter_m3_s2", positive=True)
            origin_radius = _number(origin.get("radius_m"), "active_body.radius_m", positive=True)
            target_radius = _number(target.get("radius_m"), "target_body.radius_m", positive=True)
            r1 = origin_radius + parking_altitude
            r2 = target_orbit_radius
            transfer_a = (r1 + r2) / 2.0
            v1 = _sqrt(origin_mu / r1, "origin circular speed")
            vt1 = _sqrt(origin_mu * (2.0 / r1 - 1.0 / transfer_a), "transfer departure speed")
            vt2 = _sqrt(origin_mu * (2.0 / r2 - 1.0 / transfer_a), "transfer arrival speed")
            v2 = _sqrt(origin_mu / r2, "target orbital speed")
            v_infinity = abs(v2 - vt2)
            target_parking_radius = target_radius + target_altitude
            target_circular_speed = _sqrt(target_mu / target_parking_radius, "target parking circular speed")
            target_hyperbolic_speed = _sqrt(
                v_infinity * v_infinity + 2.0 * target_mu / target_parking_radius,
                "target capture speed",
            )
            transfer_time = math.pi * _sqrt(transfer_a**3 / origin_mu, "lunar transfer time")
            phase_angle = None
            origin_longitude = _body_longitude(origin, current_ut)
            target_longitude = _body_longitude(target, current_ut)
            if origin_longitude is not None and target_longitude is not None:
                phase_angle = _normalise_degrees(target_longitude - origin_longitude)
            transfer = {
                "model": "patched_conic_body_to_moon_transfer_estimate",
                "origin_body": origin_name,
                "target_body": target_name,
                "parking_altitude_m": parking_altitude,
                "target_parking_altitude_m": target_altitude,
                "transfer_time_s": transfer_time,
                "transfer_time_days": transfer_time / 86_400.0,
                "phase_angle_current_deg": phase_angle,
                "burns": {
                    "departure_from_origin_parking_orbit": {
                        "delta_v_mps": abs(vt1 - v1),
                        "prograde_mps": vt1 - v1,
                        "description": "raise the active body's orbit to intersect the target moon's orbit",
                    },
                    "target_body_capture": {
                        "delta_v_mps": abs(target_hyperbolic_speed - target_circular_speed),
                        "retrograde_mps": -(target_hyperbolic_speed - target_circular_speed),
                        "description": "capture into a low target-body orbit before powered descent",
                    },
                },
                "total_estimated_delta_v_mps": abs(vt1 - v1) + abs(target_hyperbolic_speed - target_circular_speed),
                "assumptions": [
                    "origin and target-body orbits are treated as circular and coplanar",
                    "target-body sphere-of-influence transition is approximated",
                    "the target-body parking orbit is measured above its mean radius",
                ],
            }
            transfer_supported = True
        else:
            warnings.append(
                "the live vessel is not at the requested moon and the moon does not orbit the current body; use a separate interplanetary transfer plan first"
            )

    situation = str(flight_payload.get("situation") or "")
    landed = situation.upper() in {"LANDED", "SPLASHED"}
    return {
        "mission": "moon_soft_landing",
        "target_body": target_name,
        "active_body": origin_name,
        "active_situation": situation,
        "ready_for_soft_landing": same_body and not landed,
        "transfer_required": not same_body,
        "transfer_supported": transfer_supported,
        "transfer": transfer,
        "landing_target": {
            "latitude_deg": float(landing_latitude),
            "longitude_deg": float(landing_longitude),
            "altitude_m": 0.0,
        },
        "execution": {
            "tool": "ksp_flight_moon_soft_landing_start",
            "required": "confirm=true",
            "sequence": [
                "reach the target body's parking orbit",
                "call ksp_flight_moon_soft_landing_start",
                "wait for flight.moon_landing.started, flight.guidance.phase, and flight.touchdown",
                "call ksp_flight_guidance_stop to hand control back to KSP/player",
            ],
        },
        "warnings": warnings,
    }


def _part(
    identifier: str,
    part: str,
    *,
    parent_id: str | None = None,
    parent_attach_node: str | None = None,
    attach_node: str | None = None,
) -> dict[str, Any]:
    item: dict[str, Any] = {"id": identifier, "part": part, "stage": 0}
    if parent_id is not None:
        item.update(
            {
                "parent_id": parent_id,
                "parent_attach_node": parent_attach_node,
                "attach_node": attach_node,
                "snap_to_node": True,
            }
        )
    return item


def build_space_station(*, name: str = "MCP Orbital Station Core", template: str = "core") -> dict[str, Any]:
    """Build a stock, connected station-core editor document.

    All parts use explicit stock stack nodes.  The four lateral ports on
    ``stationHub`` are intentionally left as docking interfaces; the axial
    branch contains propellant, electrical storage, and a crew cabin.  This
    makes the result a useful orbital assembly core while leaving solar
    arrays, labs, and later modules to a human or another MCP build call.
    """

    selected = str(template or "core").strip().casefold()
    if selected != "core":
        raise MissionPlanError("supported station templates are currently: core")
    clean_name = str(name or "MCP Orbital Station Core").strip()
    if not clean_name:
        raise MissionPlanError("station name must be a non-empty string")

    parts = [
        _part("station-command", "probeCoreOcto2.v2"),
        _part("station-hub", "stationHub", parent_id="station-command", parent_attach_node="bottom", attach_node="top"),
        _part("station-port-right", "dockingPort2", parent_id="station-hub", parent_attach_node="right", attach_node="bottom"),
        _part("station-port-left", "dockingPort2", parent_id="station-hub", parent_attach_node="left", attach_node="bottom"),
        _part("station-port-front", "dockingPort2", parent_id="station-hub", parent_attach_node="front", attach_node="bottom"),
        _part("station-port-back", "dockingPort2", parent_id="station-hub", parent_attach_node="back", attach_node="bottom"),
        _part("station-service-tank", "fuelTankSmallFlat", parent_id="station-hub", parent_attach_node="bottom", attach_node="top"),
        _part("station-battery", "batteryBank", parent_id="station-service-tank", parent_attach_node="bottom", attach_node="top"),
        _part("station-habitat", "MK1CrewCabin", parent_id="station-battery", parent_attach_node="bottom", attach_node="top"),
        _part("station-port-axial", "dockingPort2", parent_id="station-habitat", parent_attach_node="bottom", attach_node="top"),
    ]
    return {
        "name": clean_name,
        "description": "Stock KSP modular orbital station core: explicit probe control, six-way hub, four lateral docking ports, axial service branch, battery and crew cabin.",
        "editor_mode": "VAB",
        "parts": parts,
        "mission": {
            "type": "space_station_core",
            "template": selected,
            "assembly_notes": [
                "launch this core or deliver it as an orbital module after editor validation",
                "the four lateral docking ports are reserved for later modules",
                "add solar arrays, science labs, and additional crew modules as separate connected payloads",
            ],
        },
    }


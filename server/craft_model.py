"""Pure-Python validation and normalisation for the KSP craft document.

The KSP plugin validates again before mutating the editor. Keeping a small
validator here lets an MCP client receive useful errors before a request
crosses the game boundary, while the plugin remains the source of truth for
actual part names and attachment nodes.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from typing import Any


class CraftValidationError(ValueError):
    """Raised when a craft document cannot be safely normalised."""


def _vector(value: Any, length: int, field: str, default: Sequence[float]) -> list[float]:
    if value is None:
        value = default
    if isinstance(value, (str, bytes)) or not isinstance(value, Sequence):
        raise CraftValidationError(f"{field} must be an array of {length} numbers")
    if len(value) != length:
        raise CraftValidationError(f"{field} must contain exactly {length} numbers")
    result: list[float] = []
    for index, item in enumerate(value):
        if isinstance(item, bool) or not isinstance(item, (int, float)):
            raise CraftValidationError(f"{field}[{index}] must be a number")
        result.append(float(item))
    return result


def _non_empty_string(value: Any, field: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise CraftValidationError(f"{field} must be a non-empty string")
    return value.strip()


def _compact_part_name(value: str) -> str:
    """Return a comparison-friendly KSP part name without punctuation."""

    return "".join(character for character in value.lower() if character.isalnum())


def _looks_like_probe_core(part_name: str) -> bool:
    compact = _compact_part_name(part_name)
    return "probecore" in compact or "probestack" in compact


def _looks_like_crewed_command_part(part_name: str) -> bool:
    compact = _compact_part_name(part_name)
    if _looks_like_probe_core(part_name):
        return False
    return any(token in compact for token in ("pod", "cockpit", "landercabin", "commandchair"))


def normalise_part_spec(raw: Mapping[str, Any], index: int | None = None) -> dict[str, Any]:
    if not isinstance(raw, Mapping):
        raise CraftValidationError("each part must be an object")

    fallback_id = f"part-{(index or 0) + 1}"
    identifier = raw.get("id", fallback_id)
    identifier = _non_empty_string(identifier, "part.id")

    part_name = raw.get("part", raw.get("part_name"))
    part_name = _non_empty_string(part_name, f"part {identifier}.part")

    parent_id = raw.get("parent_id", raw.get("parent"))
    if parent_id is not None:
        parent_id = _non_empty_string(parent_id, f"part {identifier}.parent_id")

    parent_node = raw.get("parent_attach_node", raw.get("parent_node"))
    if parent_node is not None:
        parent_node = _non_empty_string(parent_node, f"part {identifier}.parent_attach_node")

    attach_node = raw.get("attach_node", raw.get("child_attach_node"))
    if attach_node is not None:
        attach_node = _non_empty_string(attach_node, f"part {identifier}.attach_node")

    stage = raw.get("stage", 0)
    if isinstance(stage, bool) or not isinstance(stage, int) or stage < 0:
        raise CraftValidationError(f"part {identifier}.stage must be a non-negative integer")

    result: dict[str, Any] = {
        "id": identifier,
        "part": part_name,
        "parent_id": parent_id,
        "parent_attach_node": parent_node,
        "attach_node": attach_node,
        "position": _vector(raw.get("position"), 3, f"part {identifier}.position", (0.0, 0.0, 0.0)),
        "rotation": _vector(raw.get("rotation"), 4, f"part {identifier}.rotation", (0.0, 0.0, 0.0, 1.0)),
        "stage": stage,
    }

    # These fields are intentionally passed through. The game plugin decides
    # which action names, variants and module data are valid for the loaded
    # part, but retaining them makes the document forward-compatible.
    for key in ("action_groups", "symmetry_group", "symmetry_index", "variant", "custom_data", "snap_to_node"):
        if key in raw:
            result[key] = raw[key]

    return result


def validate_craft_document(
    raw: Mapping[str, Any],
    *,
    require_connected: bool = False,
) -> dict[str, Any]:
    if not isinstance(raw, Mapping):
        raise CraftValidationError("craft must be an object")

    name = raw.get("name", "MCP Craft")
    if not isinstance(name, str) or not name.strip():
        raise CraftValidationError("craft.name must be a non-empty string")
    description = raw.get("description", "")
    if not isinstance(description, str):
        raise CraftValidationError("craft.description must be a string")

    editor_mode = raw.get("editor_mode", raw.get("mode", "VAB"))
    if editor_mode not in ("VAB", "SPH"):
        raise CraftValidationError("craft.editor_mode must be VAB or SPH")

    raw_parts = raw.get("parts", [])
    if not isinstance(raw_parts, Sequence) or isinstance(raw_parts, (str, bytes)):
        raise CraftValidationError("craft.parts must be an array")

    parts: list[dict[str, Any]] = []
    ids: set[str] = set()
    for index, raw_part in enumerate(raw_parts):
        part = normalise_part_spec(raw_part, index)
        if part["id"] in ids:
            raise CraftValidationError(f"duplicate part id: {part['id']}")
        ids.add(part["id"])
        parts.append(part)

    roots = [part for part in parts if part["parent_id"] is None]
    missing_parents = [
        (part["id"], part["parent_id"])
        for part in parts
        if part["parent_id"] is not None and part["parent_id"] not in ids
    ]
    if missing_parents:
        formatted = ", ".join(f"{child} -> {parent}" for child, parent in missing_parents)
        raise CraftValidationError(f"unknown parent ids: {formatted}")
    if require_connected and parts and len(roots) != 1:
        raise CraftValidationError(f"a connected craft must have exactly one root, found {len(roots)}")

    # Detect parent cycles without assuming that parts arrive in tree order.
    parent_by_id = {part["id"]: part["parent_id"] for part in parts}
    for start in parent_by_id:
        seen: set[str] = set()
        cursor: str | None = start
        while cursor is not None:
            if cursor in seen:
                raise CraftValidationError(f"parent cycle detected at part {cursor}")
            seen.add(cursor)
            cursor = parent_by_id.get(cursor)

    warnings: list[str] = []
    for part in parts:
        if part["parent_id"] is not None and (
            part["parent_attach_node"] is None or part["attach_node"] is None
        ):
            warnings.append(
                f"part {part['id']} has a parent but no complete attachment-node pair; "
                "the game may leave it unconnected"
            )

    # A crewed pod is a valid KSP part, but a newly generated editor craft has
    # no crew roster to populate.  KSP therefore shows its native "no control
    # source" dialog at launch even though the part contains ModuleCommand.
    # Keep this as a preflight warning (rather than rejecting every possible
    # crewed save) and make the robust, visionless fix explicit: add a stock
    # probe core or load a craft that already has crew assigned.
    has_probe_core = any(_looks_like_probe_core(part["part"]) for part in parts)
    if parts and not has_probe_core:
        crewed_command_parts = [
            part["id"] for part in parts if _looks_like_crewed_command_part(part["part"])
        ]
        if crewed_command_parts:
            warnings.append(
                "no explicit probe core found for crew-capable command part(s) "
                + ", ".join(crewed_command_parts)
                + "; KSP may require crew at launch. Add probeCoreOcto2.v2 (recommended for no-visual builds) "
                "or load a craft with an assigned crew roster."
            )

    return {
        "name": name.strip(),
        "description": description,
        "editor_mode": editor_mode,
        "parts": parts,
        "warnings": warnings,
    }


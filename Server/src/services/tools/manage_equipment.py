"""Project-automation wrapper for Project Storm equipment and performance-part authoring."""

from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.refresh_unity import send_mutation


@mcp_for_unity_tool(
    group="equipment",
    description=(
        "Creates, updates, validates, repairs, deprecates, and purges Project Storm equipment "
        "families, their mount variants, standalone legacy or handheld equipment, and performance "
        "parts. This is Project Automation: inspect and preflight with get_equipment_catalog first, "
        "then request the required local Unity approval before mutation. The Unity authoring service "
        "is authoritative for catalog, composition, registry, transaction, and gameplay rules."
    ),
    annotations=ToolAnnotations(
        title="Manage Equipment",
        destructiveHint=True,
    ),
)
async def manage_equipment(
    ctx: Context,
    action: Annotated[
        Literal[
            "create",
            "update",
            "compose",
            "thumbnail",
            "validate",
            "repair",
            "deprecate",
            "purge",
        ],
        "Equipment mutation action.",
    ],
    spec: Annotated[
        dict[str, Any] | None,
        "Inline versioned equipment specification. Use instead of spec_path.",
    ] = None,
    spec_path: Annotated[
        str | None,
        "Assets-relative JSON equipment specification path. Use instead of spec.",
    ] = None,
    target: Annotated[
        Any | None,
        "Exact content or asset identity for update, thumbnail, validate, deprecate, repair, or purge.",
    ] = None,
    operation_id: Annotated[
        str | None,
        "Stable caller operation identifier for idempotency and repair tracking.",
    ] = None,
    expected_revision: Annotated[
        str | None,
        "Expected target revision/fingerprint. Unity rejects stale updates unless conflict_policy permits it.",
    ] = None,
    conflict_policy: Annotated[
        Literal["fail", "prefer_spec", "prefer_project"] | None,
        "Reviewed conflict behavior for revision drift or managed-field differences.",
    ] = None,
    execute: Annotated[
        bool | None,
        "Explicit confirmation for destructive repair or purge work after Unity preflight.",
    ] = None,
) -> dict[str, Any]:
    """Forward a project-authoring mutation through reload-aware Unity transport."""
    if spec is not None and spec_path is not None:
        return {"success": False, "message": "Provide exactly one of spec or spec_path."}

    params: dict[str, Any] = {"action": action}
    for key, value in {
        "spec": spec,
        "spec_path": spec_path,
        "target": target,
        "operation_id": operation_id,
        "expected_revision": expected_revision,
        "conflict_policy": conflict_policy,
        "execute": execute,
    }.items():
        if value is not None:
            params[key] = value

    unity_instance = await get_unity_instance_from_context(ctx)
    try:
        result = await send_mutation(ctx, unity_instance, "manage_equipment", params)
    except TimeoutError:
        return {
            "success": False,
            "message": "Unity connection timeout. Check that Unity is running and responsive.",
        }
    except Exception as exc:
        return {"success": False, "message": f"Equipment management request failed: {exc}"}

    if isinstance(result, dict):
        return result
    if hasattr(result, "model_dump"):
        return result.model_dump()
    return {"success": False, "message": f"Unexpected Unity response: {result}"}

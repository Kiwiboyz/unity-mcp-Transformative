"""Read-only discovery and preflight wrapper for Project Storm equipment authoring."""

from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


@mcp_for_unity_tool(
    group="equipment",
    description=(
        "Inspects Project Storm equipment authoring without changing the project. "
        "Discover approved optimized Props prefabs, safe primitive and material rules, equipment "
        "templates, existing families and performance parts, registry candidates, and operation "
        "state. Use propose_composition and preflight to review a versioned equipment specification "
        "before requesting project automation with manage_equipment."
    ),
    annotations=ToolAnnotations(
        title="Get Equipment Catalog",
        readOnlyHint=True,
        destructiveHint=False,
    ),
)
async def get_equipment_catalog(
    ctx: Context,
    action: Annotated[
        Literal[
            "status",
            "catalog",
            "search",
            "describe",
            "inspect",
            "resolve",
            "propose_composition",
            "preflight",
            "registry_candidates",
            "operation_status",
            "coverage",
            "refresh_cache",
        ],
        "Read-only equipment catalog action.",
    ],
    spec: Annotated[
        dict[str, Any] | None,
        "Inline versioned equipment specification for propose_composition or preflight.",
    ] = None,
    spec_path: Annotated[
        str | None,
        "Assets-relative JSON equipment specification path, alternative to spec.",
    ] = None,
    target: Annotated[
        Any | None,
        "Exact asset/content identity, Unity GUID/path, or discovery hint for inspect or resolve.",
    ] = None,
    query: Annotated[str | None, "Read-only text query for search or resolve."] = None,
    content_kind: Annotated[
        Literal["family_equipment", "legacy_equipment", "handheld_equipment", "performance_part"] | None,
        "Optional equipment content-kind filter.",
    ] = None,
    template: Annotated[
        str | None,
        "Optional exact template identifier for describe, catalog, or preflight context.",
    ] = None,
    operation_id: Annotated[str | None, "Operation identifier for operation_status."] = None,
    page: Annotated[int | None, "One-based result page."] = None,
    page_size: Annotated[int | None, "Result page size from 1 to 200."] = None,
) -> dict[str, Any]:
    """Forward a strictly read-only equipment authoring request to Unity."""
    if spec is not None and spec_path is not None:
        return {"success": False, "message": "Provide exactly one of spec or spec_path."}

    params: dict[str, Any] = {"action": action}
    for key, value in {
        "spec": spec,
        "spec_path": spec_path,
        "target": target,
        "query": query,
        "content_kind": content_kind,
        "template": template,
        "operation_id": operation_id,
        "page": page,
        "page_size": page_size,
    }.items():
        if value is not None:
            params[key] = value

    unity_instance = await get_unity_instance_from_context(ctx)
    try:
        result = await send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "get_equipment_catalog",
            params,
        )
    except TimeoutError:
        return {
            "success": False,
            "message": "Unity connection timeout. Check that Unity is running and responsive.",
        }
    except Exception as exc:
        return {"success": False, "message": f"Equipment catalog request failed: {exc}"}

    if isinstance(result, dict):
        return result
    if hasattr(result, "model_dump"):
        return result.model_dump()
    return {"success": False, "message": f"Unexpected Unity response: {result}"}

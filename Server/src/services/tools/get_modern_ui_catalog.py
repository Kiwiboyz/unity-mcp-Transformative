"""Read-only MCP wrapper for Modern UI Pack discovery and preflight."""
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


@mcp_for_unity_tool(
    group="ui",
    description=(
        "Inspects the installed Michsky Modern UI Pack without changing the project. "
        "Use status/catalog/describe before building UI, resolve_style_source to safely find nearby style donors, "
        "and preflight to validate a versioned screen specification before requesting project automation."
    ),
    annotations=ToolAnnotations(title="Get Modern UI Catalog", readOnlyHint=True, destructiveHint=False),
)
async def get_modern_ui_catalog(
    ctx: Context,
    action: Annotated[Literal[
        "status", "catalog", "describe", "inspect", "preflight", "resolve_style_source", "coverage", "refresh_cache"
    ], "Read-only catalog action."],
    package_path: Annotated[str | None, "Optional Assets-relative Modern UI Pack folder."] = None,
    family: Annotated[str | None, "Optional component or prefab family filter for catalog."] = None,
    component: Annotated[str | None, "Modern UI component type for describe or style resolution."] = None,
    target: Annotated[Any | None, "Exact target object reference, hierarchy path, or name for inspect/preflight context."] = None,
    name: Annotated[str | None, "Approximate style donor name for resolve_style_source."] = None,
    near: Annotated[Any | None, "Optional nearby target that improves style-donor ranking."] = None,
    spec: Annotated[dict[str, Any] | None, "Inline Modern UI spec version 1 for preflight."] = None,
    spec_path: Annotated[str | None, "Assets-relative JSON specification path for preflight."] = None,
    include_examples: Annotated[bool | None, "Include demo/example content in catalog results."] = None,
    page: Annotated[int | None, "One-based catalog page."] = None,
    page_size: Annotated[int | None, "Catalog page size, 1-200."] = None,
) -> dict[str, Any]:
    if spec is not None and spec_path is not None:
        return {"success": False, "message": "Provide exactly one of spec or spec_path."}
    params: dict[str, Any] = {"action": action}
    for key, value in {
        "package_path": package_path, "family": family, "component": component, "target": target,
        "name": name, "near": near, "spec": spec, "spec_path": spec_path,
        "include_examples": include_examples, "page": page, "page_size": page_size,
    }.items():
        if value is not None:
            params[key] = value
    unity_instance = await get_unity_instance_from_context(ctx)
    result = await send_with_unity_instance(
        async_send_command_with_retry, unity_instance, "get_modern_ui_catalog", params
    )
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}

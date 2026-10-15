"""Read-only Project Storm gameplay authoring (Storm Stories, Recipes, Mission Designer); paired Unity bridge."""
from typing import Annotated, Any, Literal
from fastmcp import Context
from mcp.types import ToolAnnotations
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance

DOMAINS = ("story", "recipe", "mission_designer")
READS = {
    "story": ("schema", "list", "read", "validate", "bindings", "references"),
    "recipe": ("schema", "read", "validate"),
    "mission_designer": ("visual.query", "preview.bindings", "recipe.summary", "simulation.status", "story.live"),
}


def operation(domain: str, request: dict[str, Any]) -> str:
    return str(request.get("action" if domain == "mission_designer" else "op") or "")


@mcp_for_unity_tool(
    group="gameplay_authoring",
    description=("Inspect Project Storm gameplay content through the same services as the Mission Designer window. "
                 "story: schema (node catalogue, conditions, outcomes, edit operations), list, read, validate, bindings, references. "
                 "recipe: schema, read, validate. mission_designer: visual.query, preview.bindings, recipe.summary, simulation.status, story.live. "
                 "No mutations; use manage_gameplay_authoring for changes."),
    annotations=ToolAnnotations(title="Get Gameplay Authoring", readOnlyHint=True, destructiveHint=False),
)
async def get_gameplay_authoring(
    ctx: Context,
    domain: Annotated[Literal["story", "recipe", "mission_designer"], "Authoring domain."],
    request: Annotated[dict[str, Any], "Domain request: {'op': ...} for story/recipe, {'action': ...} for mission_designer, plus that operation's fields (for example assetPath)."],
) -> dict[str, Any]:
    if domain not in DOMAINS:
        return {"success": False, "message": "Unknown gameplay authoring domain."}
    if not isinstance(request, dict):
        return {"success": False, "message": "request must be an object."}
    op = operation(domain, request)
    if op not in READS[domain]:
        return {"success": False, "message": f"get_gameplay_authoring does not allow {domain} operation '{op}'. Changes use manage_gameplay_authoring."}
    try:
        instance = await get_unity_instance_from_context(ctx)
        result = await send_with_unity_instance(async_send_command_with_retry, instance, "get_gameplay_authoring", {"domain": domain, "request": request})
    except Exception as exc:  # transport failures are reported to the client, never raised
        return {"success": False, "message": f"Gameplay authoring request failed: {exc}"}
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}

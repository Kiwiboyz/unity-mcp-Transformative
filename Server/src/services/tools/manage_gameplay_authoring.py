"""Project Storm gameplay authoring changes (Storm Stories, Recipes, Mission Designer); paired Unity bridge."""
from typing import Annotated, Any, Literal
from fastmcp import Context
from mcp.types import ToolAnnotations
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.refresh_unity import send_mutation

DOMAINS = ("story", "recipe", "mission_designer")
MUTATIONS = {
    "story": ("create", "duplicate", "edit", "save", "publish", "anchor.add", "route.add", "route.set", "scene.adopt"),
    "recipe": ("create", "duplicate", "save", "publish", "create-catalog"),
    "mission_designer": ("visual.set", "visual.focus", "simulation.configure", "simulation.start", "simulation.stop",
                         "simulation.restart", "simulation.pause", "simulation.step", "story.open", "story.test"),
}


def operation(domain: str, request: dict[str, Any]) -> str:
    return str(request.get("action" if domain == "mission_designer" else "op") or "")


@mcp_for_unity_tool(
    group="gameplay_authoring",
    description=("Create and edit Project Storm gameplay content through the same services as the Mission Designer window "
                 "(Unity Undo, validation, draft then publish). story: create, duplicate, edit (semantic edits to phases, nodes, "
                 "settings, conditions, routes and branches), save, publish, anchor.add, route.add, route.set, scene.adopt. "
                 "recipe: create, duplicate, save, publish, create-catalog. mission_designer: visual.set, visual.focus, simulation.*, "
                 "story.open, story.test. Story and Recipe changes need an operationId (a retry returns the first result). "
                 "Project Automation approval required."),
    annotations=ToolAnnotations(title="Manage Gameplay Authoring", readOnlyHint=False, destructiveHint=True),
)
async def manage_gameplay_authoring(
    ctx: Context,
    domain: Annotated[Literal["story", "recipe", "mission_designer"], "Authoring domain."],
    request: Annotated[dict[str, Any], "Domain request: {'op': ..., 'operationId': ...} for story/recipe, {'action': ...} for mission_designer, plus that operation's fields."],
) -> dict[str, Any]:
    if domain not in DOMAINS:
        return {"success": False, "message": "Unknown gameplay authoring domain."}
    if not isinstance(request, dict):
        return {"success": False, "message": "request must be an object."}
    op = operation(domain, request)
    if op not in MUTATIONS[domain]:
        return {"success": False, "message": f"manage_gameplay_authoring does not allow {domain} operation '{op}'. Reads use get_gameplay_authoring."}
    if domain in ("story", "recipe") and not str(request.get("operationId") or "").strip():
        return {"success": False, "message": "Story and Recipe changes need an operationId so a retry can't apply them twice."}
    try:
        instance = await get_unity_instance_from_context(ctx)
        result = await send_mutation(ctx, instance, "manage_gameplay_authoring", {"domain": domain, "request": request})
    except Exception as exc:
        return {"success": False, "message": f"Gameplay authoring request failed: {exc}"}
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}

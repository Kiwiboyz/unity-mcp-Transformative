"""Project Storm performance evidence collection; paired with ManageDiagnostics.cs."""
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance

Action = Literal[
    "capabilities", "benchmark_start", "benchmark_status", "benchmark_stop",
    "benchmark_report", "benchmark_compare", "benchmark_analyze", "frame_breakdown", "settings_capture",
    "settings_compare", "memory_objects", "memory_references", "memory_compare",
    "storm_diagnostics", "renderer_audit", "visual_start", "visual_status",
    "visual_stop", "visual_compare",
]


@mcp_for_unity_tool(
    group="profiling",
    description=(
        "Collect Project Storm performance evidence. Requires Unity Project Automation approval. "
        "benchmark_start(options.duration_seconds=60) returns immediately; poll benchmark_status, "
        "then benchmark_report(options.artifact) for paged timings and 60/90 FPS misses. "
        "Raw recordings above 512 MiB require benchmark_analyze(artifact,allow_large_recording=true); import can consume several GB of RAM. "
        "frame_breakdown(options.frame, optional artifact) uses a profiler_frame ID from benchmark_report.profiler_frames, "
        "not a game_frame ID; exposes CPU samples and GPU pass timing where captured. "
        "settings_capture returns an artifact; settings_compare and benchmark_compare take baseline/candidate artifact names. "
        "memory_objects(snapshot_path), memory_references(snapshot_path,kind=native|managed,index,depth), "
        "memory_compare(snapshot_a,snapshot_b) read real project-local .snap objects with Memory Profiler 1.1. "
        "Files over 512 MiB require allow_large_snapshot=true; reads can stall the editor and use multiples of file size in RAM. "
        "storm_diagnostics reads measured project counters; renderer_audit ranks loaded families and LODs. "
        "visual_start(count=12,interval_frames=30,optional baseline) captures Game View PNG sequences and optionally replays camera poses. "
        "Reset scene/weather/inputs before replay. Poll visual_status. visual_compare(baseline,candidate) flags mismatches and exports difference images. "
        "allow_mismatch=true permits explicitly non-matched image review. settings_capture(include_details=true) includes full settings inline. "
        "All parameters above are keys in options. Pagination uses offset/limit. "
        "Artifacts are filenames under Library/McpDiagnostics, not Assets; binary recordings and screenshots consume disk. "
        "Unavailable values include reasons. Editor benchmarks are not standalone build certification."
    ),
    annotations=ToolAnnotations(title="Project Storm Diagnostics", readOnlyHint=False, destructiveHint=True),
)
async def manage_diagnostics(
    ctx: Context,
    action: Annotated[Action, "Diagnostic operation."],
    options: Annotated[dict[str, Any] | None, "Operation parameters, artifact filenames and pagination described above."] = None,
) -> dict[str, Any]:
    from typing import get_args
    if action not in get_args(Action):
        return {"success": False, "message": "Unknown diagnostic action."}
    options = options or {}
    if not isinstance(options, dict):
        return {"success": False, "message": "options must be an object."}
    for key in ("allow_large_snapshot", "allow_large_recording", "allow_mismatch", "include_details"):
        if key in options and type(options[key]) is not bool:
            return {"success": False, "message": f"{key} must be boolean."}
    required = {
        "benchmark_report": ("artifact",), "benchmark_analyze": ("artifact",), "benchmark_compare": ("baseline", "candidate"),
        "settings_compare": ("baseline", "candidate"), "visual_compare": ("baseline", "candidate"),
        "memory_objects": ("snapshot_path",), "memory_references": ("snapshot_path", "kind", "index"),
        "memory_compare": ("snapshot_a", "snapshot_b"),
    }
    for key in required.get(action, ()):
        if key not in options or options[key] is None or options[key] == "":
            return {"success": False, "message": f"{key} is required for {action}."}
    if action == "memory_references" and options.get("kind") not in ("native", "managed"):
        return {"success": False, "message": "kind must be native or managed."}
    bounds = {"duration_seconds": (1, 600), "limit": (1, 500), "offset": (0, 2147483647),
              "count": (1, 120), "interval_frames": (2, 600), "depth": (1, 10),
              "frame": (0, 2147483647), "index": (0, 2147483647)}
    for key, (low, high) in bounds.items():
        if key in options and (type(options[key]) is not int or not low <= options[key] <= high):
            return {"success": False, "message": f"{key} must be an integer in [{low}, {high}]."}
    if action == "visual_compare" and options.get("limit", 12) > 120:
        return {"success": False, "message": "visual_compare limit must be at most 120."}
    unity_instance = await get_unity_instance_from_context(ctx)
    result = await send_with_unity_instance(async_send_command_with_retry, unity_instance,
                                          "manage_diagnostics", {"action": action, "options": options})
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}

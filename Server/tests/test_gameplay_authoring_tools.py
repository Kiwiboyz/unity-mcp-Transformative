"""Contract tests for the Project Storm gameplay authoring MCP wrappers."""

import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

from services.registry import get_registered_tools
import services.tools.get_gameplay_authoring as get_mod
import services.tools.manage_gameplay_authoring as manage_mod


def run_async(coro):
    loop = asyncio.new_event_loop()
    try:
        asyncio.set_event_loop(loop)
        return loop.run_until_complete(coro)
    finally:
        loop.close()
        asyncio.set_event_loop(None)


def test_read_routes_story_validate_unchanged(monkeypatch):
    captured = {}

    async def fake_send(sender, instance, command, params):
        captured.update(command=command, instance=instance, params=params)
        return {"success": True, "data": {"status": "Applied"}}

    monkeypatch.setattr(get_mod, "send_with_unity_instance", fake_send)
    monkeypatch.setattr(get_mod, "get_unity_instance_from_context", AsyncMock(return_value="unity-1"))
    request = {"op": "validate", "assetPath": "Assets/GameplayRebuild/Stories/First Response.asset"}
    response = run_async(get_mod.get_gameplay_authoring(SimpleNamespace(), "story", request))
    assert response["success"] is True
    assert captured == {"command": "get_gameplay_authoring", "instance": "unity-1", "params": {"domain": "story", "request": request}}


def test_read_tool_refuses_every_mutation_without_transport(monkeypatch):
    async def fail_send(*_args, **_kwargs):
        raise AssertionError("transport must not be called")

    monkeypatch.setattr(get_mod, "send_with_unity_instance", fail_send)
    for domain, request in (("story", {"op": "edit"}), ("story", {"op": "publish"}), ("recipe", {"op": "save"}),
                            ("mission_designer", {"action": "story.test"})):
        response = run_async(get_mod.get_gameplay_authoring(SimpleNamespace(), domain, request))
        assert response["success"] is False and "manage_gameplay_authoring" in response["message"]


def test_manage_routes_story_edit_as_mutation(monkeypatch):
    captured = {}

    async def fake_mutation(ctx, instance, command, params):
        captured.update(command=command, instance=instance, params=params)
        return {"success": True, "data": {"status": "Applied"}}

    monkeypatch.setattr(manage_mod, "send_mutation", fake_mutation)
    monkeypatch.setattr(manage_mod, "get_unity_instance_from_context", AsyncMock(return_value="unity-1"))
    request = {"op": "edit", "operationId": "op-1", "assetPath": "Assets/S.asset", "edits": [{"op": "phase.add", "value": "Shelter"}]}
    response = run_async(manage_mod.manage_gameplay_authoring(SimpleNamespace(), "story", request))
    assert response["success"] is True
    assert captured["command"] == "manage_gameplay_authoring"
    assert captured["params"] == {"domain": "story", "request": request}


def test_manage_requires_operation_id_and_known_operation(monkeypatch):
    async def fail_mutation(*_args, **_kwargs):
        raise AssertionError("mutation must not be sent")

    monkeypatch.setattr(manage_mod, "send_mutation", fail_mutation)
    missing = run_async(manage_mod.manage_gameplay_authoring(SimpleNamespace(), "story", {"op": "edit", "assetPath": "Assets/S.asset"}))
    assert missing["success"] is False and "operationId" in missing["message"]
    unknown = run_async(manage_mod.manage_gameplay_authoring(SimpleNamespace(), "story", {"op": "execute_code", "operationId": "x"}))
    assert unknown["success"] is False and "does not allow" in unknown["message"]
    read = run_async(manage_mod.manage_gameplay_authoring(SimpleNamespace(), "story", {"op": "validate", "operationId": "x"}))
    assert read["success"] is False and "get_gameplay_authoring" in read["message"]


def test_simulation_controls_need_no_operation_id(monkeypatch):
    sent = {}

    async def fake_mutation(ctx, instance, command, params):
        sent.update(params=params)
        return {"success": True}

    monkeypatch.setattr(manage_mod, "send_mutation", fake_mutation)
    monkeypatch.setattr(manage_mod, "get_unity_instance_from_context", AsyncMock(return_value=None))
    response = run_async(manage_mod.manage_gameplay_authoring(
        SimpleNamespace(), "mission_designer", {"action": "story.test", "assetPath": "Assets/S.asset", "startPhase": 2}))
    assert response == {"success": True}
    assert sent["params"]["request"]["startPhase"] == 2


def test_transport_failure_is_reported(monkeypatch):
    async def failing(*_args, **_kwargs):
        raise RuntimeError("Unity went away")

    monkeypatch.setattr(get_mod, "send_with_unity_instance", failing)
    monkeypatch.setattr(get_mod, "get_unity_instance_from_context", AsyncMock(return_value=None))
    response = run_async(get_mod.get_gameplay_authoring(SimpleNamespace(), "story", {"op": "list"}))
    assert response["success"] is False and "Unity went away" in response["message"]


def test_tools_are_registered_in_their_group():
    tools = {item["name"]: item for item in get_registered_tools()}
    assert tools["get_gameplay_authoring"]["group"] == "gameplay_authoring"
    assert tools["manage_gameplay_authoring"]["group"] == "gameplay_authoring"

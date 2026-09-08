"""Contract tests for the Project Storm environment MCP wrappers."""
import asyncio

from .test_helpers import DummyContext
import services.tools.get_environment_catalog as catalog_mod
import services.tools.manage_environment as manage_mod


def run_async(coro):
    loop = asyncio.new_event_loop()
    try:
        asyncio.set_event_loop(loop)
        return loop.run_until_complete(coro)
    finally:
        loop.close()
        asyncio.set_event_loop(None)


def test_catalog_routes_read_only_request(monkeypatch):
    captured = {}

    async def fake_send(_sender, instance, command, params):
        captured.update(instance=instance, command=command, params=params)
        return {"success": True, "data": {"compatible": True}}

    monkeypatch.setattr(catalog_mod, "send_with_unity_instance", fake_send)
    response = run_async(catalog_mod.get_environment_catalog(
        DummyContext(), "catalog", family="weather", page=2,
    ))
    assert response["success"] is True
    assert captured["command"] == "get_environment_catalog"
    assert captured["params"] == {"action": "catalog", "family": "weather", "page": 2}


def test_catalog_rejects_two_spec_sources_without_transport(monkeypatch):
    async def fail_send(*_args, **_kwargs):
        raise AssertionError("transport must not be called")

    monkeypatch.setattr(catalog_mod, "send_with_unity_instance", fail_send)
    response = run_async(catalog_mod.get_environment_catalog(
        DummyContext(), "preflight", spec={"schema_version": 1}, spec_path="Assets/Environment/spec.json",
    ))
    assert response["success"] is False
    assert "exactly one" in response["message"]


def test_manage_routes_goal_preview_through_project_automation(monkeypatch):
    captured = {}

    async def fake_send(ctx, instance, command, params):
        captured.update(ctx=ctx, instance=instance, command=command, params=params)
        return {"success": True, "data": {"status": "previewed"}}

    monkeypatch.setattr(manage_mod, "send_mutation", fake_send)
    response = run_async(manage_mod.manage_environment(
        DummyContext(), "preview_goal", goal_id="goal-1", spec={"schema_version": 1}, confirm_play_mode=True,
    ))
    assert response["success"] is True
    assert captured["command"] == "manage_environment"
    assert captured["params"] == {
        "action": "preview_goal", "goal_id": "goal-1", "spec": {"schema_version": 1},
        "confirm_play_mode": True,
    }


def test_manage_rejects_two_spec_sources_without_mutation(monkeypatch):
    async def fail_send(*_args, **_kwargs):
        raise AssertionError("mutation must not be called")

    monkeypatch.setattr(manage_mod, "send_mutation", fail_send)
    response = run_async(manage_mod.manage_environment(
        DummyContext(), "apply", spec={"schema_version": 1}, spec_path="Assets/Environment/spec.json",
    ))
    assert response["success"] is False
    assert "exactly one" in response["message"]


def test_manage_forwards_agent_visual_assessment(monkeypatch):
    captured = {}

    async def fake_send(_ctx, _instance, _command, params):
        captured.update(params)
        return {"success": True}

    monkeypatch.setattr(manage_mod, "send_mutation", fake_send)
    response = run_async(manage_mod.manage_environment(
        DummyContext(), "preview_goal", goal_id="goal-2",
        assessment={"score": 0.86, "close_enough": True, "note": "foreground is readable"},
    ))
    assert response["success"] is True
    assert captured["assessment"]["close_enough"] is True

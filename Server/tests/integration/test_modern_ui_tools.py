"""Contract tests for the Modern UI catalog and mutation MCP wrappers."""
import asyncio

from .test_helpers import DummyContext
import services.tools.get_modern_ui_catalog as catalog_mod
import services.tools.manage_modern_ui as manage_mod


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
        return {"success": True, "data": {"installed": True}}

    monkeypatch.setattr(catalog_mod, "send_with_unity_instance", fake_send)
    response = run_async(catalog_mod.get_modern_ui_catalog(DummyContext(), "catalog", family="Button", page=2))

    assert response["success"] is True
    assert captured["command"] == "get_modern_ui_catalog"
    assert captured["params"] == {"action": "catalog", "family": "Button", "page": 2}


def test_catalog_rejects_inline_and_file_spec_without_transport(monkeypatch):
    async def fail_send(*_args, **_kwargs):
        raise AssertionError("transport must not be called")

    monkeypatch.setattr(catalog_mod, "send_with_unity_instance", fail_send)
    response = run_async(catalog_mod.get_modern_ui_catalog(
        DummyContext(), "preflight", spec={"spec_version": "1"}, spec_path="Assets/UI/spec.json"
    ))
    assert response["success"] is False
    assert "exactly one" in response["message"]


def test_manage_routes_apply_through_project_automation(monkeypatch):
    captured = {}

    async def fake_send(ctx, instance, command, params):
        captured.update(ctx=ctx, instance=instance, command=command, params=params)
        return {"success": True, "data": {"status": "complete"}}

    monkeypatch.setattr(manage_mod, "send_mutation", fake_send)
    spec = {"spec_version": "1", "nodes": [{"id": "save", "component": "ButtonManager"}]}
    response = run_async(manage_mod.manage_modern_ui(DummyContext(), "apply", spec=spec, mode="create"))

    assert response["success"] is True
    assert captured["command"] == "manage_modern_ui"
    assert captured["params"]["spec"] == spec
    assert captured["params"]["mode"] == "create"


def test_manage_rejects_two_spec_sources_without_mutation(monkeypatch):
    async def fail_send(*_args, **_kwargs):
        raise AssertionError("mutation must not be called")

    monkeypatch.setattr(manage_mod, "send_mutation", fail_send)
    response = run_async(manage_mod.manage_modern_ui(
        DummyContext(), "apply", spec={"spec_version": "1"}, spec_path="Assets/UI/spec.json"
    ))
    assert response["success"] is False
    assert "exactly one" in response["message"]

"""Contract tests for the Project Storm equipment MCP wrappers."""

import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

from services.registry import get_registered_tools
import services.tools.get_equipment_catalog as catalog_mod
import services.tools.manage_equipment as manage_mod


def run_async(coro):
    loop = asyncio.new_event_loop()
    try:
        asyncio.set_event_loop(loop)
        return loop.run_until_complete(coro)
    finally:
        loop.close()
        asyncio.set_event_loop(None)


def test_catalog_routes_read_only_preflight_with_exact_optional_values(monkeypatch):
    captured = {}

    async def fake_send(sender, instance, command, params):
        captured.update(sender=sender, instance=instance, command=command, params=params)
        return {"success": True, "data": {"valid": True}}

    monkeypatch.setattr(catalog_mod, "send_with_unity_instance", fake_send)
    monkeypatch.setattr(
        catalog_mod,
        "get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )

    response = run_async(catalog_mod.get_equipment_catalog(
        SimpleNamespace(),
        "preflight",
        spec={"schema_version": 1, "content_kind": "family_equipment"},
        content_kind="family_equipment",
        template="mountable",
        page=2,
        page_size=25,
    ))

    assert response == {"success": True, "data": {"valid": True}}
    assert captured["command"] == "get_equipment_catalog"
    assert captured["instance"] == "unity-instance-1"
    assert captured["params"] == {
        "action": "preflight",
        "spec": {"schema_version": 1, "content_kind": "family_equipment"},
        "content_kind": "family_equipment",
        "template": "mountable",
        "page": 2,
        "page_size": 25,
    }


def test_catalog_rejects_two_spec_sources_without_transport(monkeypatch):
    async def fail_send(*_args, **_kwargs):
        raise AssertionError("transport must not be called")

    monkeypatch.setattr(catalog_mod, "send_with_unity_instance", fail_send)
    response = run_async(catalog_mod.get_equipment_catalog(
        SimpleNamespace(),
        "preflight",
        spec={"schema_version": 1},
        spec_path="Assets/Equipment/spec.json",
    ))

    assert response["success"] is False
    assert "exactly one" in response["message"]


def test_catalog_reports_transport_exception(monkeypatch):
    async def failing_send(*_args, **_kwargs):
        raise RuntimeError("Unity rejected the request")

    monkeypatch.setattr(catalog_mod, "send_with_unity_instance", failing_send)
    response = run_async(catalog_mod.get_equipment_catalog(SimpleNamespace(), "status"))

    assert response["success"] is False
    assert "Equipment catalog request failed" in response["message"]


def test_manage_routes_mutation_with_revision_and_operation_identity(monkeypatch):
    captured = {}

    async def fake_send(ctx, instance, command, params):
        captured.update(ctx=ctx, instance=instance, command=command, params=params)
        return {"success": True, "data": {"operation_id": "op-create-1"}}

    monkeypatch.setattr(manage_mod, "send_mutation", fake_send)
    monkeypatch.setattr(
        manage_mod,
        "get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-2"),
    )

    context = SimpleNamespace()
    response = run_async(manage_mod.manage_equipment(
        context,
        "create",
        spec={"schema_version": 1, "content_kind": "family_equipment"},
        operation_id="op-create-1",
        expected_revision="catalog-revision-8",
        conflict_policy="fail",
    ))

    assert response == {"success": True, "data": {"operation_id": "op-create-1"}}
    assert captured["ctx"] is context
    assert captured["instance"] == "unity-instance-2"
    assert captured["command"] == "manage_equipment"
    assert captured["params"] == {
        "action": "create",
        "spec": {"schema_version": 1, "content_kind": "family_equipment"},
        "operation_id": "op-create-1",
        "expected_revision": "catalog-revision-8",
        "conflict_policy": "fail",
    }


def test_manage_rejects_two_spec_sources_without_mutation(monkeypatch):
    async def fail_send(*_args, **_kwargs):
        raise AssertionError("mutation transport must not be called")

    monkeypatch.setattr(manage_mod, "send_mutation", fail_send)
    response = run_async(manage_mod.manage_equipment(
        SimpleNamespace(),
        "update",
        spec={"schema_version": 1},
        spec_path="Assets/Equipment/spec.json",
    ))

    assert response["success"] is False
    assert "exactly one" in response["message"]


def test_manage_reports_transport_exception(monkeypatch):
    async def failing_send(*_args, **_kwargs):
        raise RuntimeError("Project Automation approval required")

    monkeypatch.setattr(manage_mod, "send_mutation", failing_send)
    response = run_async(manage_mod.manage_equipment(SimpleNamespace(), "validate"))

    assert response["success"] is False
    assert "Equipment management request failed" in response["message"]


def test_equipment_tools_are_registered_in_the_dedicated_group():
    tools = {item["name"]: item for item in get_registered_tools()}
    assert tools["get_equipment_catalog"]["group"] == "equipment"
    assert tools["manage_equipment"]["group"] == "equipment"

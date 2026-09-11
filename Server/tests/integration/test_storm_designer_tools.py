"""Focused paired-tool transport, bounds and explicit-commit contracts."""
import asyncio
import importlib
import pytest
from .test_helpers import DummyContext

NAMES = ["get_tornado_catalog", "manage_tornado", "get_storm_wind_catalog", "manage_storm_wind"]

@pytest.mark.parametrize("name", NAMES)
def test_routes_semantic_request_to_matching_handler(name, monkeypatch):
    mod = importlib.import_module("services.tools." + name)
    captured = {}
    async def send(*args):
        captured["command"], captured["params"] = args[-2:]
        return {"success": True}
    mutation = name.startswith("manage")
    monkeypatch.setattr(mod, "send_mutation" if mutation else "send_with_unity_instance", send)
    action = "control" if mutation else "catalog"
    result = asyncio.run(getattr(mod, name)(DummyContext(), action, command="pause" if mutation else None))
    assert result["success"]
    assert captured["command"] == name
    assert captured["params"]["action"] == action

@pytest.mark.parametrize("name", NAMES)
def test_rejects_oversized_sample_without_transport(name):
    mod = importlib.import_module("services.tools." + name)
    action = "control" if name.startswith("manage") else "sample_batch"
    result = asyncio.run(getattr(mod, name)(DummyContext(), action, positions=[[0, 0, 0]] * 257))
    assert result["success"] is False

@pytest.mark.parametrize("name", ["manage_tornado", "manage_storm_wind"])
def test_commit_requires_exact_preview_hash(name):
    mod = importlib.import_module("services.tools." + name)
    result = asyncio.run(getattr(mod, name)(DummyContext(), "commit_goal", goal_id="test"))
    assert result["success"] is False
    assert "candidate_hash" in result["message"]

@pytest.mark.parametrize("name", ["get_tornado_catalog", "get_storm_wind_catalog"])
def test_inspection_cannot_send_mutation(name):
    mod = importlib.import_module("services.tools." + name)
    result = asyncio.run(getattr(mod, name)(DummyContext(), "commit_goal"))
    assert result["success"] is False

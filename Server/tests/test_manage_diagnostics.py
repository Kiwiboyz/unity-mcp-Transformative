"""Contracts for the paired Project Storm diagnostics tool."""
import asyncio
import importlib
from pathlib import Path
from types import SimpleNamespace
from typing import get_args
from unittest.mock import AsyncMock
import pytest

mod = importlib.import_module("services.tools.manage_diagnostics")
required = {
    "benchmark_report": {"artifact": "benchmark.json"},
    "benchmark_analyze": {"artifact": "benchmark.json"},
    "benchmark_compare": {"baseline": "a.json", "candidate": "b.json"},
    "settings_compare": {"baseline": "a.json", "candidate": "b.json"},
    "visual_compare": {"baseline": "a.json", "candidate": "b.json"},
    "memory_objects": {"snapshot_path": "MemoryCaptures/a.snap"},
    "memory_references": {"snapshot_path": "MemoryCaptures/a.snap", "kind": "native", "index": 0},
    "memory_compare": {"snapshot_a": "MemoryCaptures/a.snap", "snapshot_b": "MemoryCaptures/b.snap"},
}

@pytest.fixture
def transport(monkeypatch):
    send = AsyncMock(return_value={"success": True, "data": {}})
    monkeypatch.setattr(mod, "get_unity_instance_from_context", AsyncMock(return_value="test-unity"))
    monkeypatch.setattr(mod, "send_with_unity_instance", send)
    return send

@pytest.mark.parametrize("action", get_args(mod.Action))
def test_every_action_routes_to_matching_handler(action, transport):
    opts = required.get(action, {})
    result = asyncio.run(mod.manage_diagnostics(SimpleNamespace(), action, opts))
    assert result["success"]
    assert transport.call_args.args[1:] == ("test-unity", "manage_diagnostics", {"action": action, "options": opts})
    source = (Path(__file__).parents[2] / "MCPForUnity/Editor/Tools/Profiler/ManageDiagnostics.cs").read_text()
    assert f'case "{action}"' in source
    assert 'Capability = ToolCapability.ProjectAutomation' in source

@pytest.mark.parametrize("options", [{"duration_seconds": 0}, {"duration_seconds": True}, {"limit": 501},
    {"offset": -1}, {"count": 121}, {"interval_frames": 1}, {"frame": 1.5},
    {"allow_large_snapshot": "true"}, {"include_details": 1}, ["bad"]])
def test_invalid_input_never_reaches_unity(options, transport):
    assert not asyncio.run(mod.manage_diagnostics(SimpleNamespace(), "benchmark_start", options))["success"]
    transport.assert_not_called()

@pytest.mark.parametrize("action", list(required))
def test_required_inputs(action, transport):
    assert not asyncio.run(mod.manage_diagnostics(SimpleNamespace(), action, {}))["success"]
    transport.assert_not_called()

def test_unknown_action(transport):
    assert not asyncio.run(mod.manage_diagnostics(SimpleNamespace(), "unknown"))["success"]
    transport.assert_not_called()

def test_transport_failure_preserved(transport):
    transport.return_value = {"success": False, "error": "approval_required"}
    assert asyncio.run(mod.manage_diagnostics(SimpleNamespace(), "capabilities")) == transport.return_value

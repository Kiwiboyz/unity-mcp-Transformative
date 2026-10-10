"""get_work_area / manage_work_area: wrapper validation and the params sent to Unity."""
from __future__ import annotations

import asyncio
import math
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.get_work_area import get_work_area
from services.tools.manage_work_area import manage_work_area


@pytest.fixture
def mock_unity(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "ok"}

    for module in ("get_work_area", "manage_work_area"):
        monkeypatch.setattr(f"services.tools.{module}.get_unity_instance_from_context",
                            AsyncMock(return_value="unity-instance-1"))
        monkeypatch.setattr(f"services.tools.{module}.send_with_unity_instance", fake_send)
    return captured


def run(coroutine):
    return asyncio.run(coroutine)


def test_get_defaults_to_list(mock_unity):
    assert run(get_work_area(SimpleNamespace()))["success"] is True
    assert mock_unity["tool_name"] == "get_work_area"
    assert mock_unity["params"] == {"action": "list"}


def test_get_one_area_with_water_override(mock_unity):
    run(get_work_area(SimpleNamespace(), action="get", area="active", water_level=38.0))
    assert mock_unity["params"] == {"action": "get", "area": "active", "water_level": 38.0}


def test_get_rejects_unknown_action(mock_unity):
    result = run(get_work_area(SimpleNamespace(), action="delete"))
    assert result["success"] is False
    assert "tool_name" not in mock_unity


def test_create_polygon_sends_vertices(mock_unity):
    vertices = [[0, 0], [10, 0], [10, -5], [0, -5]]
    run(manage_work_area(SimpleNamespace(), action="create", name="Dock", vertices=vertices))
    assert mock_unity["tool_name"] == "manage_work_area"
    assert mock_unity["params"] == {"action": "create", "name": "Dock", "vertices": vertices}


def test_create_rect_needs_center_and_size(mock_unity):
    result = run(manage_work_area(SimpleNamespace(), action="create", kind="rect", center=[512, -300]))
    assert result["success"] is False
    assert "tool_name" not in mock_unity
    run(manage_work_area(SimpleNamespace(), action="create", center=[512, -300], size=[40, 20], yaw=15))
    assert mock_unity["params"] == {"action": "create", "center": [512, -300], "size": [40, 20], "yaw": 15}


@pytest.mark.parametrize("kwargs, fragment", [
    ({"action": "create", "vertices": [[0, 0], [1, 1]]}, "3 to"),
    ({"action": "create", "vertices": [[0, 0], [1, 1], [math.nan, 2]]}, "finite"),
    ({"action": "create", "center": [0, 0], "size": [0, 5]}, "size"),
    ({"action": "update"}, "needs area"),
    ({"action": "delete"}, "needs area"),
    ({"action": "update", "area": "active", "y_range": [40, 30]}, "y_range"),
    ({"action": "rename", "area": "a"}, "Unknown action"),
])
def test_manage_validation(mock_unity, kwargs, fragment):
    result = run(manage_work_area(SimpleNamespace(), **kwargs))
    assert result["success"] is False
    assert fragment in result["message"]
    assert "tool_name" not in mock_unity


def test_update_passes_only_given_fields(mock_unity):
    run(manage_work_area(SimpleNamespace(), action="update", area="Dock", y_range="auto", height_margin=8, set_active=True))
    assert mock_unity["params"] == {"action": "update", "area": "Dock", "y_range": "auto", "height_margin": 8, "set_active": True}


def test_clear_and_set_active(mock_unity):
    run(manage_work_area(SimpleNamespace(), action="set_active", area="wa_0123456789"))
    assert mock_unity["params"] == {"action": "set_active", "area": "wa_0123456789"}
    run(manage_work_area(SimpleNamespace(), action="clear"))
    assert mock_unity["params"] == {"action": "clear"}

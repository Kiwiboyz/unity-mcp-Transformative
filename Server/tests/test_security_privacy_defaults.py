import logging
from unittest.mock import patch

from core.logging_decorator import log_execution
from core.telemetry import TelemetryConfig


def test_logging_decorator_does_not_record_inputs_or_results(caplog):
    secret = "project-secret-value"

    @log_execution("safe_tool", "Tool")
    def safe_tool(value):
        return {"result": value}

    with caplog.at_level(logging.INFO):
        safe_tool(secret)

    assert secret not in caplog.text
    assert "started" in caplog.text
    assert "completed" in caplog.text


def test_telemetry_is_disabled_when_no_server_config_is_available():
    with patch("core.telemetry.import_module", side_effect=Exception("no config")):
        config = TelemetryConfig()

    assert config.enabled is False

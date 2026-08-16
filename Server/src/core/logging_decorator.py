import functools
import inspect
import logging
from typing import Callable, Any

logger = logging.getLogger("mcp-for-unity-server")


def log_execution(name: str, type_label: str):
    """Log lifecycle metadata without recording prompts, paths, or tool output."""
    def decorator(func: Callable) -> Callable:
        @functools.wraps(func)
        def _sync_wrapper(*args, **kwargs) -> Any:
            logger.info("%s '%s' started", type_label, name)
            try:
                result = func(*args, **kwargs)
                logger.info("%s '%s' completed", type_label, name)
                return result
            except Exception as e:
                logger.warning("%s '%s' failed (%s)", type_label, name, type(e).__name__)
                raise

        @functools.wraps(func)
        async def _async_wrapper(*args, **kwargs) -> Any:
            logger.info("%s '%s' started", type_label, name)
            try:
                result = await func(*args, **kwargs)
                logger.info("%s '%s' completed", type_label, name)
                return result
            except Exception as e:
                logger.warning("%s '%s' failed (%s)", type_label, name, type(e).__name__)
                raise

        return _async_wrapper if inspect.iscoroutinefunction(func) else _sync_wrapper
    return decorator

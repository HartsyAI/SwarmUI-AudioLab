#!/usr/bin/env python3
"""Dynamic engine loader and cache.

Provides ``get_engine(module_name, class_name)`` which imports the engine
module from the ``engines/`` package, instantiates the class, calls
``initialize()``, caches the result, and returns it.
"""

import importlib
import logging
import sys
from typing import Dict, Optional

from engines.base_engine import BaseAudioEngine

logger = logging.getLogger("EngineRegistry")

# Cache: keyed by "module_name:class_name"
_engine_cache: Dict[str, BaseAudioEngine] = {}


def load_engine(module_name: str, class_name: str) -> BaseAudioEngine:
    """Import a module from ``engines.<module_name>`` and instantiate
    ``<class_name>``, then call ``initialize()``.

    Returns the initialized engine.  Raises on failure.
    """
    fqn = f"engines.{module_name}"
    logger.info("Loading engine %s.%s", fqn, class_name)

    module = importlib.import_module(fqn)
    engine_class = getattr(module, class_name)

    engine: BaseAudioEngine = engine_class()
    if not engine.initialize():
        raise RuntimeError(f"Engine {class_name} from {fqn} failed to initialize")

    logger.info("Engine %s initialized successfully", class_name)
    return engine


def get_engine(module_name: str, class_name: str) -> BaseAudioEngine:
    """Return a cached engine, loading it on first access."""
    key = f"{module_name}:{class_name}"
    if key not in _engine_cache:
        _engine_cache[key] = load_engine(module_name, class_name)
    return _engine_cache[key]


def remove_engine(module_name: str, class_name: str) -> None:
    """Remove an engine from the cache after calling cleanup()."""
    key = f"{module_name}:{class_name}"
    engine = _engine_cache.pop(key, None)
    if engine is not None:
        try:
            engine.cleanup()
        except Exception as e:
            logger.warning("Error during cleanup of %s: %s", key, e)


def cleanup_all() -> None:
    """Cleanup and remove all cached engines."""
    for key in list(_engine_cache.keys()):
        engine = _engine_cache.pop(key, None)
        if engine is not None:
            try:
                engine.cleanup()
            except Exception as e:
                logger.warning("Error during cleanup of %s: %s", key, e)
    logger.info("All engines cleaned up")

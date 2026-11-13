# SPDX-License-Identifier: GPL-3.0-or-later
"""Finds the SMC-AD core add-on, however it was installed (extension repository or plain module)."""

import sys

_core = None


def find():
    """The core package (smc_ad) when it is loaded and offers the extension API, else None."""
    global _core
    if _core is not None and getattr(_core, "api", None) is not None and _core.__name__ in sys.modules:
        return _core
    _core = None
    for name, module in list(sys.modules.items()):
        if module is None or not (name == "smc_ad" or name.endswith(".smc_ad")):
            continue
        if getattr(module, "api", None) is not None and getattr(module, "batch", None) is not None:
            _core = module
            break
    return _core


def batch():
    core = find()
    return core.batch if core is not None else None

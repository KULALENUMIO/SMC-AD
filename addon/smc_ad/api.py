# SPDX-License-Identifier: GPL-3.0-or-later
"""Extension points for SMC-AD+ add-ons.

The core never imports an extension. An extension finds the core module and appends its callables
to these lists in register(), and removes them again in unregister().

  material_built  f(material)                                after a material is (re)built
                                                              (import and "Rebuild SM Materials")
  imported        f(context, objects, materials, filepath)   after an import, once all materials are built
  import_options  f(layout, context)                         draws extra options in the import dialog
  original_material  f(material) -> Material or None         the imported material a copy was made from
  before_reset    f(context, objects, materials)             before "back to import state" (clear own state)
  after_reset     f(context, objects, materials)             after it (restore own import-time state)
  export_material f(material, smcad_extras, report)          Unity export: add own data to the material's
                                                              "smcad" extras; report.add(level, material, text)
"""

import traceback

API_VERSION = 3

material_built = []
imported = []
import_options = []
original_material = []
before_reset = []
after_reset = []
export_material = []


def call(hooks, *args):
    """Runs every hook; an extension error is printed, never breaks the core."""
    for hook in list(hooks):
        try:
            hook(*args)
        except Exception:  # noqa: BLE001 - extensions must not break imports
            traceback.print_exc()

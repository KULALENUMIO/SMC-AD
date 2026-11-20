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
  export_begin    f(context, root, objects, tex_dir, folder_name, report)
                                                              Unity FBX export starts (before any mesh is touched)
  export_payload  f(context, root, payload, tex_dir, folder_name, report)
                                                              Unity FBX export: add own top-level data to the JSON
                                                              (meshes already have their export UV layers)
  export_end      f(context, root)                            Unity FBX export finished or failed (clean up)

Shader nodes of an extension that sit on a layer wire and only change it can set the custom property
"smcad_passthrough" = True: the Unity export then reads the hand-made layer through them (input of the same name).
"""

import traceback

API_VERSION = 4

material_built = []
imported = []
import_options = []
original_material = []
before_reset = []
after_reset = []
export_material = []
export_begin = []
export_payload = []
export_end = []


def call(hooks, *args):
    """Runs every hook; an extension error is printed, never breaks the core."""
    for hook in list(hooks):
        try:
            hook(*args)
        except Exception:  # noqa: BLE001 - extensions must not break imports
            traceback.print_exc()

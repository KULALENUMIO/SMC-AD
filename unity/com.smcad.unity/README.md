# SMC-AD Scrap Mechanic Importer (Unity)

> **Alpha / experimental prototype.** Updated on a whim; don't expect regular updates or support. The repository may be deleted or replaced if a proper release is ever made.

Imports `.glb` files written by the SMC-AD build of SMConverter (`SMConverterCLI -gltf [-cullhidden] [-optimizeblocks] -septype joints`).

- Requirements: Unity 6.1+ (tested 6000.3.10f1), URP 17.1+, **Linear** colour space (Gamma works but paint colours are stored as sRGB and a warning is logged).
- Install: Package Manager > + > Add package from disk... > `package.json` of this folder.
- Import: `Tools > SMC-AD > ブループリントを取り込む (.glb)...`. Assets go to `Assets/SMCAD/<name>/` (Textures, Materials, Meshes, prefab).
- Re-import keeps asset GUIDs (scene instances stay linked) and keeps material settings you changed (diffuse override etc.).
- Files exported from Blender (SMC-AD `Unity 向けに書き出し`, `<name>_smcad.glb`) also carry what was set in Blender: colour override, surface types (SMC-AD+ Surfaces: metallic, roughness, clear coat, tint, ...), materials of parts split off in Blender, and simple hand-made layer nodes. For these files Blender is the source of truth: re-importing applies the Blender values again. What could not be carried over is logged and listed on the `SMBlueprint` component (`exportReport`).
- Shaders: `SMC-AD/SM Lit` (paint colour from vertex colour, ASG specular/gloss/glow, normal map, cut-out, diffuse override, surface type values, layer parameters; clear coat via the `_CLEARCOAT` keyword) and `SMC-AD/SM Glass`.
- FBX from Blender (SMC-AD `Unity 向けに書き出し（FBX）`): `Tools > SMC-AD > Blender から書き出した FBX を取り込む...`, or put the FBX and its `<name>_textures` folder into Assets. Unity's FBX importer reads the objects, bones and weights (Generic rig on first import); SMC-AD turns the materials into SM materials and adds SMBlueprint / SMBody / SMJoint.
- Joints: the blueprint is split into bodies at its joints; every body's origin is its joint pivot. `SMJoint` (on each child body) only records the joint (type, parent body, axis, shown as a gizmo); pose and animate the transforms or bones directly.

## License
MIT License, Copyright (c) 2026 SMC-AD Project (`LICENSE.md`). This package does not contain SMConverter code, so it can be used in games of any license.

Shading model ported from SM-Converter-Plugin by QuestionableM (MIT License, see `Third Party Notices.md`).
Scrap Mechanic is a trademark of Axolot Games; this is an unofficial tool, not affiliated with Axolot Games.

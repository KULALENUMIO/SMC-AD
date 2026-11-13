// SPDX-License-Identifier: MIT
// Headless verification for the SMC-AD Unity package.
// Unity.exe -batchmode -projectPath <this> -executeMethod SMCADTest.Run -smcadOut <dir> -smcadGlb <a.glb> -smcadGlb <b.glb> ... -quit
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SMCAD;
using SMCAD.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class SMCADTest
{
    static readonly StringBuilder Report = new StringBuilder();

    static void Log(string s)
    {
        Report.AppendLine(s);
        Debug.Log("[SMCADTest] " + s);
    }

    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        string outDir = null;
        var glbs = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-smcadOut") outDir = args[i + 1];
            if (args[i] == "-smcadGlb") glbs.Add(args[i + 1]);
        }
        Directory.CreateDirectory(outDir);

        try
        {
            EditorSettings.asyncShaderCompilation = false;
            SetupUrp();
            CheckShaders();
            foreach (var glb in glbs)
                TestBlueprint(glb, outDir);
        }
        catch (Exception ex)
        {
            Log("EXCEPTION " + ex);
        }
        File.WriteAllText(Path.Combine(outDir, "unity_report.txt"), Report.ToString());
    }

    static void SetupUrp()
    {
        if (PlayerSettings.colorSpace != ColorSpace.Linear)
        {
            PlayerSettings.colorSpace = ColorSpace.Linear; // same as the user's URP projects
            Log("colour space set to Linear");
        }
        if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset)
        {
            Log("URP already active: " + GraphicsSettings.defaultRenderPipeline.name);
            return;
        }
        Directory.CreateDirectory("Assets/Settings");
        var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
        AssetDatabase.CreateAsset(rendererData, "Assets/Settings/SMCAD_Renderer.asset");
        var urp = UniversalRenderPipelineAsset.Create(rendererData);
        urp.shadowDistance = 30f;
        AssetDatabase.CreateAsset(urp, "Assets/Settings/SMCAD_URP.asset");
        GraphicsSettings.defaultRenderPipeline = urp;
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = urp;
        }
        AssetDatabase.SaveAssets();
        Log("URP set up: " + (GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.GetType().Name : "null"));
    }

    static void CheckShaders()
    {
        foreach (var name in new[] { "SMC-AD/SM Lit", "SMC-AD/SM Glass" })
        {
            var sh = Shader.Find(name);
            if (sh == null) { Log($"SHADER {name}: NOT FOUND"); continue; }
            var msgs = ShaderUtil.GetShaderMessages(sh);
            int errors = msgs.Count(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error);
            Log($"SHADER {name}: supported={sh.isSupported} hasError={ShaderUtil.ShaderHasError(sh)} errors={errors} passes={sh.passCount}");
            var labels = new List<string>();
            for (int p = 0; p < Math.Min(sh.GetPropertyCount(), 40); p++)
            {
                var attrs = sh.GetPropertyAttributes(p);
                labels.Add((attrs.Length > 0 ? "[" + string.Join(",", attrs) + "] " : "") + sh.GetPropertyDescription(p));
            }
            Log("   labels: " + string.Join(" | ", labels));
            foreach (var m in msgs.Take(10))
                Log($"   {m.severity} {m.message} ({m.file}:{m.line})");
        }
    }

    static void TestBlueprint(string glb, string outDir)
    {
        string tag = Path.GetFileNameWithoutExtension(glb);
        Log("=== " + tag);
        var t0 = DateTime.Now;
        var prefab = SMBlueprintImporter.Import(glb);
        Log($"import: {(DateTime.Now - t0).TotalSeconds:F2}s prefab={(prefab != null ? AssetDatabase.GetAssetPath(prefab) : "null")}");
        if (prefab == null) return;

        // ---- structure checks
        var filters = prefab.GetComponentsInChildren<MeshFilter>(true);
        int tris = filters.Sum(f => f.sharedMesh != null ? Enumerable.Range(0, f.sharedMesh.subMeshCount).Sum(s => (int)f.sharedMesh.GetIndexCount(s) / 3) : 0);
        int verts = filters.Sum(f => f.sharedMesh != null ? f.sharedMesh.vertexCount : 0);
        var renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
        var mats = renderers.SelectMany(r => r.sharedMaterials).Distinct().ToList();
        int nullMats = renderers.Sum(r => r.sharedMaterials.Count(m => m == null));
        var shaderNames = mats.Where(m => m != null).GroupBy(m => m.shader.name).Select(g => g.Key + "x" + g.Count());
        bool colorsOk = filters.All(f => f.sharedMesh == null || f.sharedMesh.colors.Length == f.sharedMesh.vertexCount);
        var bp = prefab.GetComponent<SMBlueprint>();
        var joints = prefab.GetComponentsInChildren<SMJoint>(true);
        var bodies = prefab.GetComponentsInChildren<SMBody>(true);
        Log($"meshes={filters.Length} tris={tris} verts={verts} materials={mats.Count} nullMaterialSlots={nullMats} shaders=[{string.Join(", ", shaderNames)}] vertexColors={colorsOk}");
        Log($"blueprint={(bp != null)} shapes={bp?.shapes.Count} jointsListed={bp?.joints.Count} bodies={bodies.Length} jointComponents={joints.Length} " +
            $"types=[{string.Join(", ", joints.GroupBy(j => j.type).Select(g => g.Key + "x" + g.Count()))}]");

        // Paint colours used (vertex colours -> sRGB hex)
        var paint = new Dictionary<string, int>();
        foreach (var f in filters)
            foreach (var c in f.sharedMesh.colors)
            {
                string hx = ColorUtility.ToHtmlStringRGB(c.gamma);
                paint[hx] = paint.TryGetValue(hx, out int n) ? n + 1 : 1;
            }
        var topPaint = paint.OrderByDescending(kv => kv.Value).Take(4).ToList();
        Log("paint colours: " + string.Join(", ", topPaint.Select(kv => "#" + kv.Key + "x" + kv.Value)));

        // ---- render
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        var lightGo = new GameObject("Sun");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 2.0f;
        light.shadows = LightShadows.Soft;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.45f, 0.48f, 0.52f);

        var bounds = new Bounds(inst.transform.position, Vector3.zero);
        bool first = true;
        foreach (var r in inst.GetComponentsInChildren<Renderer>())
        {
            if (first) { bounds = r.bounds; first = false; }
            else bounds.Encapsulate(r.bounds);
        }
        var camGo = new GameObject("Camera");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.33f, 0.36f, 0.4f);
        cam.orthographic = true;
        float size = bounds.extents.magnitude;
        cam.orthographicSize = size * 0.75f;
        Vector3 dir = new Vector3(0.6f, 0.45f, -1.0f).normalized;
        cam.transform.position = bounds.center + dir * size * 3f;
        cam.transform.LookAt(bounds.center);
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = size * 10f;

        Render(cam, Path.Combine(outDir, tag + "_unity_rest.png"));

        // Pose every bearing by 35 degrees around its joint axis (directly on the transforms), then restore
        int posed = 0;
        var restRotations = new Dictionary<Transform, Quaternion>();
        foreach (var j in inst.GetComponentsInChildren<SMJoint>())
        {
            if (j.type != SMJointType.Bearing) continue;
            restRotations[j.transform] = j.transform.localRotation;
            j.transform.localRotation *= Quaternion.AngleAxis(35f, j.localAxis);
            posed++;
        }
        if (posed > 0) Render(cam, Path.Combine(outDir, tag + "_unity_posed.png"));
        Log($"posed bearings: {posed}");
        foreach (var kv in restRotations) kv.Key.localRotation = kv.Value;

        // Diffuse override: most used paint colour -> red (on material copies, assets untouched)
        if (topPaint.Count > 0)
        {
            // Edit the material assets themselves (batch mode doesn't propagate renderer material swaps),
            // then restore them.
            ColorUtility.TryParseHtmlString("#" + topPaint[0].Key, out Color match);
            var usedMats = inst.GetComponentsInChildren<MeshRenderer>().SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToList();
            foreach (var m in usedMats)
            {
                m.SetFloat("_OvEnabled", 1f);
                m.SetColor("_OvColor", new Color(0.85f, 0.05f, 0.05f));
                m.SetFloat("_OvUseMatch", 1f);
                m.SetColor("_OvMatch", match);
                m.SetFloat("_OvTolerance", 0.02f);
            }
            Render(cam, Path.Combine(outDir, tag + "_unity_override.png"));
            foreach (var m in usedMats)
            {
                m.SetFloat("_OvEnabled", 0f);
                m.SetFloat("_OvUseMatch", 0f);
            }
            Log("override: #" + topPaint[0].Key + " -> red on " + usedMats.Count + " materials");
        }

        // ---- reimport keeps GUIDs and material edits
        string prefabPath = AssetDatabase.GetAssetPath(prefab);
        string prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
        var firstMat = mats.First(m => m != null);
        string matPath = AssetDatabase.GetAssetPath(firstMat);
        string matGuid = AssetDatabase.AssetPathToGUID(matPath);
        firstMat.SetFloat("_OvEnabled", 1f);
        firstMat.SetColor("_OvColor", Color.green);
        EditorUtility.SetDirty(firstMat);
        AssetDatabase.SaveAssets();
        SMBlueprintImporter.Import(glb);
        var reMat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        Log($"reimport: prefabGuidSame={AssetDatabase.AssetPathToGUID(prefabPath) == prefabGuid} " +
            $"materialGuidSame={AssetDatabase.AssetPathToGUID(matPath) == matGuid} " +
            $"overrideKept={(reMat != null && reMat.GetFloat("_OvEnabled") > 0.5f && reMat.GetColor("_OvColor") == Color.green)}");
        reMat.SetFloat("_OvEnabled", 0f);
        EditorUtility.SetDirty(reMat);
        AssetDatabase.SaveAssets();
    }

    // Blender export check: import, dump the material values that came from Blender, render with the same
    // camera / light as hitogata_export_test.py in Blender.
    // Unity.exe -batchmode -projectPath <this> -executeMethod SMCADTest.RenderExport -smcadOut <dir> -smcadGlb <a.glb> ... -quit
    public static void RenderExport()
    {
        var args = Environment.GetCommandLineArgs();
        string outDir = null;
        var glbs = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-smcadOut") outDir = args[i + 1];
            if (args[i] == "-smcadGlb") glbs.Add(args[i + 1]);
        }
        Directory.CreateDirectory(outDir);
        try
        {
            EditorSettings.asyncShaderCompilation = false;
            SetupUrp();
            CheckShaders();
            foreach (var glb in glbs)
            {
                string tag = Path.GetFileNameWithoutExtension(glb);
                Log("=== " + tag);
                var prefab = SMBlueprintImporter.Import(glb);
                if (prefab == null) { Log("import failed"); continue; }
                var renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
                var mats = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().OrderBy(m => m.name).ToList();
                int nullSlots = renderers.Sum(r => r.sharedMaterials.Count(m => m == null));
                int subMeshes = prefab.GetComponentsInChildren<MeshFilter>(true).Sum(f => f.sharedMesh.subMeshCount);
                Log($"materials={mats.Count} subMeshes={subMeshes} nullSlots={nullSlots}");
                foreach (var m in mats)
                {
                    if (m.shader.name != "SMC-AD/SM Lit") { Log($"MAT {m.name} shader={m.shader.name}"); continue; }
                    Log($"MAT {m.name} ov={m.GetFloat("_OvEnabled")} match={m.GetFloat("_OvUseMatch")} " +
                        $"sf={m.GetFloat("_SfEnabled")} metal={m.GetFloat("_SfTexMetallic"):F3}/{m.GetFloat("_SfPaintMetallic"):F3} " +
                        $"rough={m.GetFloat("_SfRoughScale"):F3}+{m.GetFloat("_SfRoughOffset"):F3} coat={m.GetFloat("_SfCoat"):F3} " +
                        $"tint={m.GetVector("_SfTint")}x{m.GetFloat("_SfTintStrength"):F2} clearcoatKw={m.IsKeywordEnabled("_CLEARCOAT")} " +
                        $"LBase={m.GetVector("_LBaseOp")} LMetal={m.GetVector("_LMetalOp")} LRough={m.GetVector("_LRoughOp")}/{m.GetVector("_LRoughA").x:F2}");
                }
                foreach (var j in prefab.GetComponentsInChildren<SMJoint>(true).OrderBy(j => j.name))
                    Log($"JOINT {j.name} parent={j.transform.parent.name} parentBody={j.parentBody} type={j.type} " +
                        $"worldPos={j.transform.position.ToString("F4")}");
                var bp = prefab.GetComponent<SMBlueprint>();
                Log("report lines: " + (bp != null ? bp.exportReport.Count : -1));
                if (bp != null) foreach (var line in bp.exportReport) Log("  " + line);

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                var lightGo = new GameObject("Sun");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 2.0f;
                light.shadows = LightShadows.Soft;
                lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                var ambient = new Color(0.45f, 0.48f, 0.52f);
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = ambient;
                // Uniform reflections of the ambient colour, like Blender's uniform world
                var cube = new Cubemap(16, TextureFormat.RGBA32, false);
                var px = Enumerable.Repeat(ambient, 16 * 16).ToArray();
                foreach (CubemapFace f in Enum.GetValues(typeof(CubemapFace)))
                    if (f != CubemapFace.Unknown) cube.SetPixels(px, f);
                cube.Apply();
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                RenderSettings.customReflectionTexture = cube;
                // Batch mode does not update the default reflection: a custom probe around everything does
                var probeGo = new GameObject("Reflection");
                var probe = probeGo.AddComponent<ReflectionProbe>();
                probe.mode = ReflectionProbeMode.Custom;
                probe.customBakedTexture = cube;
                probe.size = Vector3.one * 10000f;
                probe.intensity = 1f;
                probe.importance = 100;

                var bounds = new Bounds();
                bool first = true;
                foreach (var r in inst.GetComponentsInChildren<Renderer>())
                {
                    if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds);
                }
                var cam = new GameObject("Camera").AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = ambient;
                cam.orthographic = true;
                float size = bounds.extents.magnitude;
                cam.orthographicSize = size * 0.75f;
                Vector3 dir = new Vector3(0.6f, 0.45f, -1.0f).normalized;
                cam.transform.position = bounds.center + dir * size * 3f;
                cam.transform.LookAt(bounds.center);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = size * 10f;
                // Shader variants (e.g. _CLEARCOAT) compile on first use: render once to warm up, then for real
                Render(cam, Path.Combine(outDir, "warmup.png"));
                Render(cam, Path.Combine(outDir, "warmup.png"));
                Render(cam, Path.Combine(outDir, "unity_" + tag + ".png"));
            }
        }
        catch (Exception ex)
        {
            Log("EXCEPTION " + ex);
        }
        File.WriteAllText(Path.Combine(outDir, "unity_export_report.txt"), Report.ToString());
    }

    // SMC-AD FBX from Blender: import through SMFbxPostprocessor, compare with the Blender state, render.
    // Unity.exe -batchmode -projectPath <this> -executeMethod SMCADTest.FbxTest -smcadOut <dir> -smcadFbx <x.fbx> -smcadState <blender_state.json> -quit
    public static void FbxTest()
    {
        var args = Environment.GetCommandLineArgs();
        string outDir = args[Array.IndexOf(args, "-smcadOut") + 1];
        string fbx = args[Array.IndexOf(args, "-smcadFbx") + 1];
        string statePath = args[Array.IndexOf(args, "-smcadState") + 1];
        Directory.CreateDirectory(outDir);
        try
        {
            EditorSettings.asyncShaderCompilation = false;
            SetupUrp();
            var model = SMFbxPostprocessor.Import(fbx);
            Log("model: " + (model != null ? AssetDatabase.GetAssetPath(model) : "null"));
            if (model == null) return;
            var state = MiniJson.Parse(File.ReadAllText(statePath)) as Dictionary<string, object>;
            var bodies = MiniJson.Obj(state, "bodies");

            var renderers = model.GetComponentsInChildren<Renderer>(true);
            var mats = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToList();
            Log($"renderers={renderers.Length} skinned={model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length} " +
                $"materials={mats.Count} smLit={mats.Count(m => m.shader.name == "SMC-AD/SM Lit")} smGlass={mats.Count(m => m.shader.name == "SMC-AD/SM Glass")} " +
                $"withDif={mats.Count(m => m.HasProperty("_DifMap") && m.GetTexture("_DifMap") != null)} override={mats.Count(m => m.HasProperty("_OvEnabled") && m.GetFloat("_OvEnabled") > 0.5f)} " +
                $"surface={mats.Count(m => m.HasProperty("_SfEnabled") && m.GetFloat("_SfEnabled") > 0.5f)}");
            var bp = model.GetComponent<SMBlueprint>();
            Log($"SMBlueprint={(bp != null)} name={bp?.blueprintName} report={bp?.exportReport.Count} SMBody={model.GetComponentsInChildren<SMBody>(true).Length} SMJoint={model.GetComponentsInChildren<SMJoint>(true).Length}");
            var avatar = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(model)).OfType<Avatar>().FirstOrDefault();
            Log($"avatar={(avatar != null ? (avatar.isHuman ? "humanoid" : "generic") : "none")}");

            Vector3 ToUnity(List<object> v) => new Vector3(-(float)(double)v[0], (float)(double)v[2], -(float)(double)v[1]);
            var transforms = model.GetComponentsInChildren<Transform>(true);
            float maxPos = 0f, maxCenter = 0f, maxAxis = 0f;
            int partUv = 0, checkedBodies = 0;
            foreach (var kv in bodies)
            {
                var t = transforms.FirstOrDefault(x => x.name == kv.Key);
                if (t == null) { Log("  missing in Unity: " + kv.Key); continue; }
                checkedBodies++;
                var b = kv.Value as Dictionary<string, object>;
                maxPos = Mathf.Max(maxPos, (t.position - ToUnity(MiniJson.Arr(b, "pos"))).magnitude);
                Mesh mesh = t.TryGetComponent<MeshFilter>(out var mfx) ? mfx.sharedMesh : null;
                t.TryGetComponent<SkinnedMeshRenderer>(out var smr);
                Vector3 c;
                if (smr != null) { var baked = new Mesh(); smr.BakeMesh(baked, true); c = t.TransformPoint(baked.vertices.Aggregate(Vector3.zero, (a, v) => a + v) / baked.vertexCount); mesh = smr.sharedMesh; }
                else c = t.TransformPoint(mesh.vertices.Aggregate(Vector3.zero, (a, v) => a + v) / mesh.vertexCount);
                // FBX may split vertices along seams, so the centroid moves a little; compare loosely
                maxCenter = Mathf.Max(maxCenter, (c - ToUnity(MiniJson.Arr(b, "center"))).magnitude);
                var joint = t.GetComponent<SMJoint>();
                if (joint != null)
                    maxAxis = Mathf.Max(maxAxis, Vector3.Angle(t.TransformDirection(joint.localAxis), ToUnity(MiniJson.Arr(b, "axis"))));
                var uv2 = new List<Vector2>(); mesh.GetUVs(2, uv2);
                if (uv2.Count == mesh.vertexCount && uv2.Count > 0 && Mathf.Abs(uv2[0].x - Mathf.Round(uv2[0].x)) < 1e-3f) partUv++;
            }
            Log($"bodies checked={checkedBodies} maxOriginDiff={maxPos:F5} maxCentroidDiff={maxCenter:F4} maxJointAxisAngle={maxAxis:F3} partUvInUV2={partUv}");

            // render like the Blender test camera
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 2f; light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var ambient = new Color(0.45f, 0.48f, 0.52f);
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = ambient;
            var cube = new Cubemap(16, TextureFormat.RGBA32, false);
            var px = Enumerable.Repeat(ambient, 256).ToArray();
            foreach (CubemapFace f in Enum.GetValues(typeof(CubemapFace))) if (f != CubemapFace.Unknown) cube.SetPixels(px, f);
            cube.Apply();
            var probe = new GameObject("Reflection").AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Custom; probe.customBakedTexture = cube; probe.size = Vector3.one * 10000f;
            var camInfo = MiniJson.Obj(state, "camera");
            Vector3 center = ToUnity(MiniJson.Arr(camInfo, "center"));
            float ext = (float)MiniJson.Num(camInfo, "extent", 5);
            var cam = new GameObject("Camera").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = ambient;
            cam.orthographic = true; cam.orthographicSize = ext * 1.0f;
            cam.transform.position = center + new Vector3(0.35f, 0.25f, 1f).normalized * ext * 3f;
            cam.transform.LookAt(center);
            cam.nearClipPlane = 0.01f; cam.farClipPlane = ext * 20f;
            void Shot(string file)
            {
                var rt = new RenderTexture(900, 1000, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                for (int i = 0; i < 3; i++) { cam.targetTexture = rt; cam.Render(); }
                RenderTexture.active = rt;
                var tex = new Texture2D(900, 1000, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 900, 1000), 0, 0); tex.Apply();
                File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG());
                RenderTexture.active = null; cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex);
                Log("rendered " + file);
            }
            Shot("unity_fbx.png");
            // move a bone: the bodies on it (and the skinned body) must follow
            var bone = inst.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.StartsWith("bone_") && t.childCount > 0 && t.GetComponentInChildren<Renderer>() != null && t.parent != null && t.parent.name.StartsWith("bone_"));
            if (bone != null)
            {
                bone.localRotation *= Quaternion.Euler(0f, 0f, 45f);
                Log("rotated " + bone.name);
                Shot("unity_fbx_bone.png");
            }
        }
        catch (Exception ex)
        {
            Log("EXCEPTION " + ex);
        }
        File.WriteAllText(Path.Combine(outDir, "unity_fbx_report.txt"), Report.ToString());
    }

    // Motion test: import the user's posed FBX, then raise both arms over the head ("banzai") by rotating the
    // shoulder bodies about their joint axes. -smcadArms "<left shoulder>,<right shoulder>" -smcadHands "<left hand>,<right hand>"
    public static void BanzaiTest()
    {
        var args = Environment.GetCommandLineArgs();
        string Arg(string k) => args[Array.IndexOf(args, k) + 1];
        string outDir = Arg("-smcadOut");
        Directory.CreateDirectory(outDir);
        try
        {
            EditorSettings.asyncShaderCompilation = false;
            SetupUrp();
            var model = SMFbxPostprocessor.Import(Arg("-smcadFbx"));
            var state = MiniJson.Parse(File.ReadAllText(Arg("-smcadState"))) as Dictionary<string, object>;
            var bodies = MiniJson.Obj(state, "bodies");
            Vector3 ToUnity(List<object> v) => new Vector3(-(float)(double)v[0], (float)(double)v[2], -(float)(double)v[1]);

            var mats = model.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToList();
            Log($"model={AssetDatabase.GetAssetPath(model)} materials={mats.Count} smLit={mats.Count(m => m.shader.name.StartsWith("SMC-AD"))} " +
                $"withDif={mats.Count(m => m.HasProperty("_DifMap") && m.GetTexture("_DifMap") != null)} SMBody={model.GetComponentsInChildren<SMBody>(true).Length} SMJoint={model.GetComponentsInChildren<SMJoint>(true).Length} " +
                $"blueprint={model.GetComponent<SMBlueprint>()?.blueprintName}");

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            var all = inst.GetComponentsInChildren<Transform>(true);
            Transform T(string n) => all.FirstOrDefault(t => t.name == n);
            float maxPos = 0f, maxAxis = 0f;
            foreach (var kv in bodies)
            {
                var t = T(kv.Key);
                if (t == null) { Log("missing: " + kv.Key); continue; }
                var b = kv.Value as Dictionary<string, object>;
                maxPos = Mathf.Max(maxPos, (t.position - ToUnity(MiniJson.Arr(b, "pos"))).magnitude);
                if (t.TryGetComponent<SMJoint>(out var j))
                    maxAxis = Mathf.Max(maxAxis, Vector3.Angle(t.TransformDirection(j.localAxis), ToUnity(MiniJson.Arr(b, "axis"))));
            }
            Log($"pose matches Blender: maxOriginDiff={maxPos:F5} maxJointAxisAngle={maxAxis:F3}");

            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 2f; light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(35f, 150f, 0f);
            var ambient = new Color(0.45f, 0.48f, 0.52f);
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = ambient;
            var cube = new Cubemap(16, TextureFormat.RGBA32, false);
            var px = Enumerable.Repeat(ambient, 256).ToArray();
            foreach (CubemapFace f in Enum.GetValues(typeof(CubemapFace))) if (f != CubemapFace.Unknown) cube.SetPixels(px, f);
            cube.Apply();
            var probe = new GameObject("Reflection").AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Custom; probe.customBakedTexture = cube; probe.size = Vector3.one * 10000f;
            var camInfo = MiniJson.Obj(state, "camera");
            Vector3 center = ToUnity(MiniJson.Arr(camInfo, "center"));
            float ext = (float)MiniJson.Num(camInfo, "extent", 3) * 1.25f;
            var cam = new GameObject("Camera").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = ambient;
            cam.orthographic = true; cam.orthographicSize = ext; cam.nearClipPlane = 0.01f; cam.farClipPlane = ext * 20f;
            void Shot(string file, Vector3 dir)
            {
                cam.transform.position = center + dir.normalized * ext * 4f;
                cam.transform.LookAt(center);
                var rt = new RenderTexture(900, 1000, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                for (int i = 0; i < 3; i++) { cam.targetTexture = rt; cam.Render(); }
                RenderTexture.active = rt;
                var tex = new Texture2D(900, 1000, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 900, 1000), 0, 0); tex.Apply();
                File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG());
                RenderTexture.active = null; cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex);
            }
            var front = new Vector3(0.35f, 0.15f, 1f);
            var side = new Vector3(1f, 0.1f, 0.05f);
            Shot("1_as_exported_front.png", front);
            Shot("1_as_exported_side.png", side);

            // Banzai: shoulders rotate about their joint axes; the direction is chosen so the hands go forward (+Z) first
            var shoulders = Arg("-smcadArms").Split(',').Select(T).ToArray();
            var hands = Arg("-smcadHands").Split(',').Select(T).ToArray();
            for (int i = 0; i < shoulders.Length; i++)
            {
                var s = shoulders[i]; var h = hands[i];
                var j = s.GetComponent<SMJoint>();
                Vector3 axis = j != null ? j.localAxis : Vector3.right;
                var rest = s.localRotation;
                float zBefore = h.position.z, yBefore = h.position.y;
                s.localRotation = rest * Quaternion.AngleAxis(90f, axis);
                float sign = h.position.z > zBefore ? 1f : -1f;
                s.localRotation = rest * Quaternion.AngleAxis(170f * sign, axis);
                Log($"{s.name}: axis(local)={axis.ToString("F2")} direction={(sign > 0 ? "+" : "-")} hand y {yBefore:F2} -> {h.position.y:F2} (shoulder y {s.position.y:F2})");
            }
            Shot("2_banzai_front.png", front);
            Shot("2_banzai_side.png", side);
        }
        catch (Exception ex)
        {
            Log("EXCEPTION " + ex);
        }
        File.WriteAllText(Path.Combine(outDir, "unity_banzai_report.txt"), Report.ToString());
    }

    // FBX saved straight into the project (FBX and textures arrive in the same refresh).
    public static void FbxDirect()
    {
        var args = Environment.GetCommandLineArgs();
        string outDir = args[Array.IndexOf(args, "-smcadOut") + 1];
        string fbx = args[Array.IndexOf(args, "-smcadFbx") + 1];
        try
        {
            string dst = "Assets/SMCAD_Direct";
            if (Directory.Exists(dst)) AssetDatabase.DeleteAsset(dst);
            Directory.CreateDirectory(dst);
            string stem = Path.GetFileNameWithoutExtension(fbx);
            File.Copy(fbx, Path.Combine(dst, Path.GetFileName(fbx)), true);
            string srcTex = Path.Combine(Path.GetDirectoryName(fbx), stem + "_textures");
            foreach (var f in Directory.GetFiles(srcTex, "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(dst, stem + "_textures", f.Substring(srcTex.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(f, to, true);
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(dst + "/" + Path.GetFileName(fbx));
            var mats = model.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToList();
            Log($"direct: model={(model != null)} materials={mats.Count} smLit={mats.Count(m => m.shader.name.StartsWith("SMC-AD"))} " +
                $"withDif={mats.Count(m => m.HasProperty("_DifMap") && m.GetTexture("_DifMap") != null)} withNor={mats.Count(m => m.HasProperty("_NorMap") && m.GetTexture("_NorMap") != null)} " +
                $"SMBlueprint={(model.GetComponent<SMBlueprint>() != null)}");
            var nor = AssetDatabase.FindAssets("t:Texture2D", new[] { dst + "/" + stem + "_textures/nor" }).Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault();
            if (nor != null) Log("normal map import type: " + ((TextureImporter)AssetImporter.GetAtPath(nor)).textureType);
        }
        catch (Exception ex)
        {
            Log("EXCEPTION " + ex);
        }
        File.WriteAllText(Path.Combine(outDir, "unity_direct_report.txt"), Report.ToString());
    }

    // Rigged robot trial: SMC-AD glb (for the SM materials) + Blender FBX (armature, bone-parented bodies, pose).
    // Unity.exe -batchmode -projectPath <this> -executeMethod SMCADTest.RigTest -smcadOut <dir> -smcadGlb <smcad.glb> -smcadFbx <rig.fbx> -quit
    public static void RigTest()
    {
        var args = Environment.GetCommandLineArgs();
        string outDir = args[Array.IndexOf(args, "-smcadOut") + 1];
        string glb = args[Array.IndexOf(args, "-smcadGlb") + 1];
        string fbxSrc = args[Array.IndexOf(args, "-smcadFbx") + 1];
        Directory.CreateDirectory(outDir);
        try
        {
            EditorSettings.asyncShaderCompilation = false;
            SetupUrp();
            var smPrefab = SMBlueprintImporter.Import(glb);
            string matFolder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(smPrefab)).Replace('\\', '/') + "/Materials";
            Log("SM materials folder: " + matFolder + " count=" + Directory.GetFiles(matFolder, "*.mat").Length);

            Directory.CreateDirectory("Assets/SMCAD_FBX");
            string fbxPath = "Assets/SMCAD_FBX/" + Path.GetFileName(fbxSrc);
            File.Copy(fbxSrc, fbxPath, true);
            AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate);
            var mi = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
            mi.animationType = ModelImporterAnimationType.Generic;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            mi.importAnimation = false;
            int mapped = 0, unmapped = 0;
            var embedded = AssetDatabase.LoadAllAssetRepresentationsAtPath(fbxPath).OfType<Material>().Select(m => m.name).Distinct().ToList();
            foreach (var name in embedded)
            {
                string candidate = matFolder + "/" + name + ".mat";
                var sm = AssetDatabase.LoadAssetAtPath<Material>(candidate);
                if (sm == null)
                {
                    // Blender may add ".001" etc.; Unity asset names may differ in sanitising
                    sm = Directory.GetFiles(matFolder, "*.mat").Select(p => AssetDatabase.LoadAssetAtPath<Material>(p.Replace('\\', '/')))
                                  .FirstOrDefault(m => m != null && (m.name == name || name.StartsWith(m.name + ".")));
                }
                if (sm != null) { mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), name), sm); mapped++; }
                else { unmapped++; Log("  unmapped FBX material: " + name); }
            }
            mi.SaveAndReimport();
            Log($"FBX materials: {embedded.Count} mapped={mapped} unmapped={unmapped}");

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var avatar = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Avatar>().FirstOrDefault();
            Log($"avatar={(avatar != null ? avatar.name : "none")} valid={(avatar != null && avatar.isValid)} generic={(avatar != null && !avatar.isHuman)}");

            var sb = new StringBuilder();
            void Tree(Transform t, int d)
            {
                var mr = t.GetComponent<MeshRenderer>();
                var mf = t.GetComponent<MeshFilter>();
                sb.AppendLine($"{new string(' ', d * 2)}{t.name} pos={t.localPosition.ToString("F2")} rot={t.localEulerAngles.ToString("F0")} scale={t.localScale.ToString("F2")}" +
                              (mf != null ? $" mesh(v={mf.sharedMesh.vertexCount}, colors={mf.sharedMesh.colors.Length > 0}, uv2={mf.sharedMesh.uv3.Length > 0 || mf.sharedMesh.uv2.Length > 0})" : "") +
                              (mr != null ? $" mats=[{string.Join(",", mr.sharedMaterials.Select(m => m == null ? "null" : m.shader.name.Replace("SMC-AD/", "")))}]" : ""));
                foreach (Transform c in t) Tree(c, d + 1);
            }
            Tree(model.transform, 0);
            File.WriteAllText(Path.Combine(outDir, "unity_fbx_tree.txt"), sb.ToString());
            var meshes = model.GetComponentsInChildren<MeshFilter>(true);
            var colorSample = meshes.Select(f => f.sharedMesh).Where(m => m.colors.Length > 0).Take(1).Select(m => m.colors[0]).FirstOrDefault();
            Log($"FBX: transforms={model.GetComponentsInChildren<Transform>(true).Length} meshes={meshes.Length} " +
                $"withColors={meshes.Count(f => f.sharedMesh.colors.Length > 0)} sampleColor={colorSample} " +
                $"nonSM={model.GetComponentsInChildren<MeshRenderer>(true).SelectMany(r => r.sharedMaterials).Count(m => m == null || !m.shader.name.StartsWith("SMC-AD"))}");

            // ---- render: SMC-AD glb prefab (rest pose) and the FBX (posed), same camera / light as RenderExport
            void Scene(GameObject prefab, string tag, Func<GameObject, int> poseEdit = null)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                var light = new GameObject("Sun").AddComponent<Light>();
                light.type = LightType.Directional; light.intensity = 2f; light.shadows = LightShadows.Soft;
                light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                var ambient = new Color(0.45f, 0.48f, 0.52f);
                RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = ambient;
                var cube = new Cubemap(16, TextureFormat.RGBA32, false);
                var px = Enumerable.Repeat(ambient, 256).ToArray();
                foreach (CubemapFace f in Enum.GetValues(typeof(CubemapFace))) if (f != CubemapFace.Unknown) cube.SetPixels(px, f);
                cube.Apply();
                var probe = new GameObject("Reflection").AddComponent<ReflectionProbe>();
                probe.mode = ReflectionProbeMode.Custom; probe.customBakedTexture = cube; probe.size = Vector3.one * 10000f;
                if (poseEdit != null) Log($"{tag}: pose edit moved {poseEdit(inst)} objects");
                var bounds = new Bounds(); bool first = true;
                foreach (var r in inst.GetComponentsInChildren<Renderer>()) { if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds); }
                Log($"{tag}: bounds center={bounds.center.ToString("F2")} size={bounds.size.ToString("F2")}");
                var cam = new GameObject("Camera").AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.75f, 0.77f, 0.8f);
                cam.orthographic = true; float size = bounds.extents.magnitude; cam.orthographicSize = size * 0.62f;
                cam.nearClipPlane = 0.01f; cam.farClipPlane = size * 10f;
                foreach (var (view, dir) in new[] { ("front", new Vector3(0f, 0.05f, 1f)), ("back", new Vector3(0f, 0.05f, -1f)), ("persp", new Vector3(-0.45f, 0.25f, 1f)) })
                {
                    cam.transform.position = bounds.center + dir.normalized * size * 3f;
                    cam.transform.LookAt(bounds.center);
                    Render(cam, Path.Combine(outDir, "warmup.png"));
                    Render(cam, Path.Combine(outDir, $"unity_{tag}_{view}.png"));
                }
            }
            Scene(smPrefab, "smcad_glb");
            Scene(model, "fbx_posed");
            Scene(model, "fbx_bone_test", inst =>
            {
                var bone = inst.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Objects_16");
                if (bone == null) return 0;
                bone.localRotation = bone.localRotation * Quaternion.Euler(0f, 0f, 40f);
                return bone.GetComponentsInChildren<MeshRenderer>(true).Length;
            });
        }
        catch (Exception ex)
        {
            Log("EXCEPTION " + ex);
        }
        File.WriteAllText(Path.Combine(outDir, "unity_rig_report.txt"), Report.ToString());
    }

    // Which surface setting makes parts black? Renders the imported prefab with settings switched off one by one.
    public static void DebugSurface()
    {
        var args = Environment.GetCommandLineArgs();
        string outDir = args[Array.IndexOf(args, "-smcadOut") + 1];
        string prefabPath = args[Array.IndexOf(args, "-smcadPrefab") + 1];
        EditorSettings.asyncShaderCompilation = false;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        var light = new GameObject("Sun").AddComponent<Light>();
        light.type = LightType.Directional; light.intensity = 2f; light.shadows = LightShadows.Soft;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.45f, 0.48f, 0.52f);
        var bounds = new Bounds(); bool first = true;
        foreach (var r in inst.GetComponentsInChildren<Renderer>()) { if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds); }
        var cam = new GameObject("Camera").AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.45f, 0.48f, 0.52f);
        cam.orthographic = true; float size = bounds.extents.magnitude; cam.orthographicSize = size * 0.75f;
        cam.transform.position = bounds.center + new Vector3(0.6f, 0.45f, -1f).normalized * size * 3f;
        cam.transform.LookAt(bounds.center); cam.farClipPlane = size * 10f;

        var mats = inst.GetComponentsInChildren<MeshRenderer>().SelectMany(r => r.sharedMaterials).Where(m => m != null && m.HasProperty("_SfEnabled")).Distinct().ToList();
        var saved = mats.ToDictionary(m => m, m => new Material(m));
        void Restore() { foreach (var m in mats) m.CopyPropertiesFromMaterial(saved[m]); }
        Render(cam, Path.Combine(outDir, "dbg_sf_on.png"));
        foreach (var m in mats) m.SetFloat("_SfEnabled", 0f);
        Render(cam, Path.Combine(outDir, "dbg_sf_off.png")); Restore();
        foreach (var m in mats) { m.SetFloat("_SfTexMetallic", 0f); m.SetFloat("_SfPaintMetallic", 0f); }
        Render(cam, Path.Combine(outDir, "dbg_sf_nometal.png")); Restore();
        foreach (var m in mats) m.SetFloat("_SfNoise", 0f);
        Render(cam, Path.Combine(outDir, "dbg_sf_nonoise.png")); Restore();
        foreach (var m in mats) { m.SetFloat("_SfCoat", 0f); m.DisableKeyword("_CLEARCOAT"); }
        Render(cam, Path.Combine(outDir, "dbg_sf_nocoat.png")); Restore();
        foreach (var m in mats) m.SetFloat("_SfNormalStrength", 1f);
        foreach (var m in mats) Log($"{m.name} metal={m.GetFloat("_SfTexMetallic")}/{m.GetFloat("_SfPaintMetallic")} hasDif={m.GetFloat("_HasDif")} difTex={(m.GetTexture("_DifMap") != null ? m.GetTexture("_DifMap").name : "null")}");
        File.WriteAllText(Path.Combine(outDir, "dbg_surface.txt"), Report.ToString());
    }

    public static void DebugOverride()
    {
        var args = Environment.GetCommandLineArgs();
        string outDir = args[Array.IndexOf(args, "-smcadOut") + 1];
        EditorSettings.asyncShaderCompilation = false;
        var lit = Shader.Find("SMC-AD/SM Lit");
        var data = ShaderUtil.GetShaderData(lit);
        Log($"ShaderData subshaders={data.SubshaderCount} passes(sub0)={data.GetSubshader(0).PassCount} shader.passCount={lit.passCount}");
        for (int p = 0; p < data.GetSubshader(0).PassCount; p++) Log("  pass " + data.GetSubshader(0).GetPass(p).Name);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SMCAD/cmp_b747_v4/cmp_b747_v4.prefab");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        var light = new GameObject("Sun").AddComponent<Light>();
        light.type = LightType.Directional; light.intensity = 2f; light.shadows = LightShadows.Soft;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        var r = inst.GetComponentInChildren<MeshRenderer>();
        var cam = new GameObject("Camera").AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.33f, 0.36f, 0.4f);
        cam.orthographic = true; float size = r.bounds.extents.magnitude; cam.orthographicSize = size * 0.75f;
        cam.transform.position = r.bounds.center + new Vector3(0.6f, 0.45f, -1f).normalized * size * 3f;
        cam.transform.LookAt(r.bounds.center); cam.farClipPlane = size * 10f;

        var src = r.sharedMaterial;
        var m = new Material(src);
        m.SetFloat("_OvEnabled", 1f);
        m.SetColor("_OvColor", Color.red);
        m.SetFloat("_OvUseMatch", 0f);
        r.sharedMaterial = m;
        Log($"copy: hasProp={m.HasProperty("_OvEnabled")} ovEnabled={m.GetFloat("_OvEnabled")} strength={m.GetFloat("_OvStrength")} whole={m.GetFloat("_OvWhole")} keywords=[{string.Join(",", m.shaderKeywords)}] srpBatcher={GraphicsSettings.useScriptableRenderPipelineBatching}");
        Render(cam, Path.Combine(outDir, "dbg_override_all.png"));

        var mpb = new MaterialPropertyBlock();
        r.sharedMaterial = src;
        src.SetFloat("_OvEnabled", 1f); src.SetColor("_OvColor", Color.red); src.SetFloat("_OvUseMatch", 0f);
        Render(cam, Path.Combine(outDir, "dbg_override_asset.png"));
        src.SetFloat("_OvEnabled", 0f);
        File.AppendAllText(Path.Combine(outDir, "unity_debug.txt"), Report.ToString());
    }

    public static void DebugMatch()
    {
        var args = Environment.GetCommandLineArgs();
        string outDir = args[Array.IndexOf(args, "-smcadOut") + 1];
        EditorSettings.asyncShaderCompilation = false;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SMCAD/cmp_b747_v4/cmp_b747_v4.prefab");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        var light = new GameObject("Sun").AddComponent<Light>();
        light.type = LightType.Directional; light.intensity = 2f;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        var r = inst.GetComponentInChildren<MeshRenderer>();
        var cam = new GameObject("Camera").AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
        cam.orthographic = true; float size = r.bounds.extents.magnitude; cam.orthographicSize = size * 0.75f;
        cam.transform.position = r.bounds.center + new Vector3(0.6f, 0.45f, -1f).normalized * size * 3f;
        cam.transform.LookAt(r.bounds.center); cam.farClipPlane = size * 10f;

        var mesh = inst.GetComponentInChildren<MeshFilter>().sharedMesh;
        Color raw = mesh.colors[0];
        var mat = r.sharedMaterial;
        Log($"vertex colour raw={raw} gamma={raw.gamma} colorSpace={PlayerSettings.colorSpace}");
        mat.SetFloat("_OvEnabled", 1f); mat.SetColor("_OvColor", Color.red); mat.SetFloat("_OvUseMatch", 1f);
        foreach (var (label, c, tol) in new[] { ("gamma", raw.gamma, 0.02f), ("raw", raw, 0.02f), ("gamma_tol0.3", raw.gamma, 0.3f) })
        {
            mat.SetColor("_OvMatch", c); mat.SetFloat("_OvTolerance", tol);
            string p = Path.Combine(outDir, "dbg_match_" + label + ".png");
            Render(cam, p);
            var tex = new Texture2D(2, 2); tex.LoadImage(File.ReadAllBytes(p));
            var px = tex.GetPixels32(); int red = px.Count(q => q.r > 150 && q.g < 60 && q.b < 60), model = px.Count(q => q.r + q.g + q.b > 30);
            Log($"match {label}: matColor={c} red share={(model > 0 ? red / (float)model : 0):F3}");
        }
        mat.SetFloat("_OvEnabled", 0f); mat.SetFloat("_OvUseMatch", 0f);
        File.AppendAllText(Path.Combine(outDir, "unity_debug.txt"), Report.ToString());
    }

    static void Render(Camera cam, string path)
    {
        var rt = new RenderTexture(1200, 900, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        RenderTexture.active = null;
        cam.targetTexture = null;
        UnityEngine.Object.DestroyImmediate(rt);
        UnityEngine.Object.DestroyImmediate(tex);
        Log("rendered " + Path.GetFileName(path));
    }
}

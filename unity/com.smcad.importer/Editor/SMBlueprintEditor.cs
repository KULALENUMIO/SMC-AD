// SPDX-License-Identifier: MIT
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SMCAD.Editor
{
    // Inspectors with Japanese labels for the SMC-AD components.

    [CustomEditor(typeof(SMBlueprint))]
    public class SMBlueprintEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var bp = (SMBlueprint)target;
            EditorGUILayout.LabelField("パーツの数", bp.shapes.Count.ToString());
            EditorGUILayout.LabelField("ジョイントの数", bp.joints.Count.ToString());
            EditorGUILayout.LabelField("ボディの数", bp.GetComponentsInChildren<SMBody>(true).Length.ToString());

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!File.Exists(bp.sourcePath)))
                {
                    if (GUILayout.Button("取り込み直す"))
                    {
                        Selection.activeGameObject = bp.gameObject;
                        EditorApplication.ExecuteMenuItem(SMBlueprintImporter.ReimportMenuPath);
                    }
                }
            }

            serializedObject.Update();
            if (bp.exportReport.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("Blender から持ってこられなかったもの・近似になったものがあります（下の「Blender 書き出しの結果」）",
                                        MessageType.Info);
            }
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sourcePath"), new GUIContent("元の .glb", "このプレハブを取り込んだ .glb"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("blueprintName"), new GUIContent("機体名"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("rootBody"), new GUIContent("土台のボディ", "ジョイントの根元になるボディの番号"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("shapes"), new GUIContent("パーツ一覧",
                "パーツの一覧。メッシュの UV2（TEXCOORD2.x）に、頂点ごとにこの一覧の番号が入っています"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("joints"), new GUIContent("ジョイント一覧",
                "機体のすべてのジョイント（輪になっているものも含む）"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("exportReport"), new GUIContent("Blender 書き出しの結果",
                "SMC-AD の Blender 書き出しで、持ってこられなかったもの（warning）と近似になったもの（info）"));
            serializedObject.ApplyModifiedProperties();
        }
    }

    [CustomEditor(typeof(SMJoint)), CanEditMultipleObjects]
    public class SMJointEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("type"), new GUIContent("種類"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("jointId"), new GUIContent("ジョイントID"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("uuid"), new GUIContent("UUID", "ゲームでのパーツの種類"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("parentBody"), new GUIContent("親のボディ"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("localAxis"), new GUIContent("軸（ローカル）", "ローカル座標でのジョイントの軸"));
            EditorGUILayout.HelpBox("Scrap Mechanic でボディを分けていたジョイントの情報です。動かすときは、Transform や骨を直接回してください。", MessageType.None);
            serializedObject.ApplyModifiedProperties();
        }
    }

    [CustomEditor(typeof(SMBody)), CanEditMultipleObjects]
    public class SMBodyEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("bodyIndex"), new GUIContent("ボディの番号"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("isRoot"), new GUIContent("土台のボディ", "ジョイントの根元になるボディ"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("connected"), new GUIContent("ジョイントでつながっている",
                "オフのときは、どのジョイントでも機体の他の部分とつながっていません"));
            serializedObject.ApplyModifiedProperties();
        }
    }
}

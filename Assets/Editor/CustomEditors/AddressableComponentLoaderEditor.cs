using UnityEngine;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using UnityEngine.AddressableAssets;
using BBI.Unity.Game;
using System.Collections.Generic;
using System.Linq;

[CustomEditor(typeof(AddressableComponentLoader))]
public class AddressableComponentLoaderEditor : Editor
{
    UnityEditorInternal.ReorderableList reorderableList;

    bool[] foldoutsOpen = new bool[0];

    void OnEnable()
    {
        // Setup the SerializedProperties.
        var componentValues = serializedObject.FindProperty("componentValues");

        reorderableList = new UnityEditorInternal.ReorderableList(serializedObject, componentValues);

        reorderableList.headerHeight = 0;

        reorderableList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) => {
            var element = reorderableList.serializedProperty.GetArrayElementAtIndex(index);
            
            if(foldoutsOpen.Length != reorderableList.serializedProperty.arraySize)
            {
                foldoutsOpen = new bool[reorderableList.serializedProperty.arraySize];
            }

            var position = new Rect(rect);

            EditorGUI.indentLevel++;

            foldoutsOpen[index] = EditorGUI.Foldout(new Rect(position.x, position.y, 10, EditorGUIUtility.singleLineHeight), foldoutsOpen[index], element?.FindPropertyRelative("component")?.objectReferenceValue?.GetType().ToString() ?? "Missing Component");

            // EditorGUI.PropertyField(new Rect(position.x + 200, position.y, rect.width - 200, EditorGUIUtility.singleLineHeight), element, GUIContent.none);

            if (foldoutsOpen[index])
            {
                var rect1 = new Rect(position.xMin, position.y + EditorGUIUtility.singleLineHeight, position.width, EditorGUIUtility.singleLineHeight);
                var rect2 = new Rect(position.xMin, position.y + (EditorGUIUtility.singleLineHeight * 2), position.width, EditorGUIUtility.singleLineHeight);
                var rect3 = new Rect(position.xMin, position.y + (EditorGUIUtility.singleLineHeight * 3), position.width, EditorGUIUtility.singleLineHeight);
                var rect4 = new Rect(position.xMin, position.y + (EditorGUIUtility.singleLineHeight * 4), position.width, EditorGUIUtility.singleLineHeight);

                Component selectedComponent = (Component)element.FindPropertyRelative("component").objectReferenceValue;

                var fields = selectedComponent?.GetType()
                    .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Where(fi => !fi.FieldType.IsPrimitive)
                ;

                List<string> componentFields = fields == null ? new List<string>() : fields.Select(fi => fi.Name).ToList();
                int selectedIndex = componentFields.IndexOf(element.FindPropertyRelative("field").stringValue);

                serializedObject.Update();
                EditorGUI.PropertyField(rect1, element.FindPropertyRelative("component"));
                EditorGUI.BeginProperty(rect2, new GUIContent("Field"), element.FindPropertyRelative("field"));
                element.FindPropertyRelative("field").stringValue = componentFields.ElementAtOrDefault(EditorGUI.Popup(rect2, selectedIndex, componentFields.ToArray()));
                EditorGUI.EndProperty();
                // EditorGUI.PropertyField(rect2, element.FindPropertyRelative("field"));
                EditorGUI.PropertyField(rect3, element.FindPropertyRelative("address"));
                serializedObject.ApplyModifiedProperties();

                // Label for address
                var refPath = "Unknown Asset";
                var knownPath = AssetDatabase.GUIDToAssetPath(element.FindPropertyRelative("address").stringValue);
                if (knownPath == "")
                {
                    if (LoadGameAssets.knownAssetMap.TryGetValue(element.FindPropertyRelative("address").stringValue, out knownPath))
                    {
                        refPath = knownPath;
                    }
                }
                else
                {
                    refPath = knownPath;
                }

                // Asset reference type (Custom/Vanilla)
                EditorGUI.LabelField(rect4, refPath);
            }

            EditorGUI.indentLevel--;
        };

        reorderableList.elementHeightCallback = (index) => {
            return foldoutsOpen.ElementAtOrDefault(index) ? EditorGUIUtility.singleLineHeight * 6 : EditorGUIUtility.singleLineHeight;
        };
    }

    public override void OnInspectorGUI()
    {
        // Update the serializedProperty - always do this in the beginning of OnInspectorGUI.
        serializedObject.Update ();

        // Show the custom GUI controls.
        EditorGUI.BeginChangeCheck();
        reorderableList.DoLayoutList();
        EditorGUI.EndChangeCheck();


        // Apply changes to the serializedProperty - always do this in the end of OnInspectorGUI.
        serializedObject.ApplyModifiedProperties ();

        DrawBakeScaleButton();
    }

    // Baked parts carry an AddressableComponentLoader, so this button only appears on baked parts
    // (not AddressableLoader parts, where scale-baking would fail). Shown for any non-unit scale;
    // bakes the scale into the part's meshes and resets the transform to unit scale.
    void DrawBakeScaleButton()
    {
        var go = ((AddressableComponentLoader)target).gameObject;
        var t  = go.transform;

        if (!TransformScaleBaker.IsNonUnitScale(t))
            return;

        int affected = TransformScaleBaker.CountAffected(t);
        var s = t.localScale;
        string scaleStr = TransformScaleBaker.IsUniformScale(t) ? $"{s.x:F3}" : $"({s.x:F3}, {s.y:F3}, {s.z:F3})";

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            $"Scale {scaleStr}. Bake it into the mesh geometry so joints, mass and collision are correct " +
            $"in-game ({affected} mesh{(affected == 1 ? "" : "es")} affected). The transform resets to (1,1,1) " +
            "and child positions are adjusted to keep the layout.",
            MessageType.Info);

        if (TransformScaleBaker.HasNonUniformScaleWithRotatedChildren(t))
            EditorGUILayout.HelpBox(
                "Non-uniform scale with rotated sub-meshes detected. This may SKEW rotated meshes " +
                "(non-uniform scale only composes cleanly through rotation when scaling along the " +
                "rotation axis). Verify the result; if skewed, scale uniformly instead.",
                MessageType.Warning);

        using (new EditorGUI.DisabledScope(affected == 0))
        {
            if (GUILayout.Button("Bake Transform Scale"))
            {
                int baked = TransformScaleBaker.BakeScale(go);
                EditorUtility.DisplayDialog("Bake Complete",
                    $"Baked scale into {baked} mesh(es) on '{go.name}'.\nTransform reset to (1,1,1).", "OK");
            }
        }
    }
}
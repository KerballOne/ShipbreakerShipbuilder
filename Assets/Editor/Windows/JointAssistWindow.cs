using System.Linq;
using UnityEngine;
using UnityEditor;

public class JointAssistWindow : EditorWindow
{
    GameObject objectToMove;
    GameObject targetObject;
    float overlapAmount = 0.025f;

    enum OverlapAxis { AutoDetect, PosX, NegX, PosY, NegY, PosZ, NegZ }
    OverlapAxis axis = OverlapAxis.AutoDetect;

    string statusMessage = "";
    MessageType statusType = MessageType.None;

    [MenuItem("Shipbreaker/Joint Assist", priority = 50)]
    static void Open() => GetWindow<JointAssistWindow>("Joint Assist");

    void OnSelectionChange() => Repaint();

    void OnGUI()
    {
        EditorGUILayout.LabelField("Joint Positioning Assistant", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Moves Object A toward Object B so their faces overlap by the specified amount. " +
            "Overlapping StructurePart colliders auto-joint at ship spawn.",
            MessageType.Info);

        EditorGUILayout.Space(6);

        objectToMove = (GameObject)EditorGUILayout.ObjectField("Object A  (moves)", objectToMove, typeof(GameObject), true);
        targetObject = (GameObject)EditorGUILayout.ObjectField("Object B  (stays)", targetObject, typeof(GameObject), true);

        EditorGUILayout.Space(4);

        if (GUILayout.Button("Use Scene Selection (A=first, B=second)"))
        {
            var sel = Selection.gameObjects;
            if (sel.Length >= 2)
            {
                objectToMove = sel[0];
                targetObject = sel[1];
            }
            else
            {
                statusMessage = "Select exactly 2 objects in the hierarchy first.";
                statusType = MessageType.Warning;
            }
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        overlapAmount = EditorGUILayout.FloatField("Overlap Amount (m)", overlapAmount);
        axis = (OverlapAxis)EditorGUILayout.EnumPopup("Direction", axis);

        EditorGUILayout.Space(4);

        if (objectToMove != null && targetObject != null)
            ShowPreview();

        EditorGUILayout.Space(6);

        bool canApply = objectToMove != null && targetObject != null;
        EditorGUI.BeginDisabledGroup(!canApply);

        if (GUILayout.Button("Snap Faces Flush (gap = 0)", GUILayout.Height(28)))
            ApplyMove(0f);

        if (GUILayout.Button("Apply Overlap (recommended)", GUILayout.Height(28)))
            ApplyMove(overlapAmount);

        EditorGUI.EndDisabledGroup();

        if (!string.IsNullOrEmpty(statusMessage))
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(statusMessage, statusType);
        }
    }

    void ShowPreview()
    {
        Bounds bA = GetBounds(objectToMove);
        Bounds bB = GetBounds(targetObject);
        Vector3 dir = GetDirection(bA, bB);
        float currentGap = CalculateGap(bA, bB, dir);

        EditorGUILayout.HelpBox(
            $"Move direction: {FormatDir(dir)}\n" +
            $"Current gap: {currentGap * 100f:F1} cm   " +
            $"(negative = already overlapping)\n" +
            $"Will move A by: {(currentGap + overlapAmount) * 100f:F1} cm",
            MessageType.None);
    }

    void ApplyMove(float overlap)
    {
        Bounds bA = GetBounds(objectToMove);
        Bounds bB = GetBounds(targetObject);
        Vector3 dir = GetDirection(bA, bB);
        float gap = CalculateGap(bA, bB, dir);
        float moveAmount = gap + overlap;

        Undo.RecordObject(objectToMove.transform, "Joint Assist Move");
        objectToMove.transform.position += dir * moveAmount;

        statusMessage = $"Moved '{objectToMove.name}' {moveAmount * 100f:F1} cm toward '{targetObject.name}'.";
        statusType = MessageType.Info;
        Repaint();
    }

    Vector3 GetDirection(Bounds bA, Bounds bB)
    {
        if (axis != OverlapAxis.AutoDetect)
        {
            return axis switch
            {
                OverlapAxis.PosX => Vector3.right,
                OverlapAxis.NegX => Vector3.left,
                OverlapAxis.PosY => Vector3.up,
                OverlapAxis.NegY => Vector3.down,
                OverlapAxis.PosZ => Vector3.forward,
                OverlapAxis.NegZ => Vector3.back,
                _ => Vector3.up
            };
        }

        // Auto: find which world axis has the smallest face-to-face gap
        Vector3[] axes = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        float bestGap = float.MaxValue;
        Vector3 bestDir = Vector3.up;
        foreach (var a in axes)
        {
            float g = CalculateGap(bA, bB, a);
            if (g >= 0 && g < bestGap)
            {
                bestGap = g;
                bestDir = a;
            }
        }
        // If all negative (all overlapping), pick smallest absolute gap in the direction of B
        if (bestGap == float.MaxValue)
            bestDir = ClosestCardinalDirection(bB.center - bA.center);

        return bestDir;
    }

    // Gap = space between the face of A pointing toward dir and the face of B pointing away from dir.
    // Positive = gap between them. Negative = already overlapping.
    float CalculateGap(Bounds bA, Bounds bB, Vector3 dir)
    {
        float faceA = Vector3.Dot(bA.center, dir) + ReachInDir(bA, dir);
        float faceB = Vector3.Dot(bB.center, dir) - ReachInDir(bB, dir);
        return faceB - faceA;
    }

    float ReachInDir(Bounds b, Vector3 dir)
    {
        return Mathf.Abs(b.extents.x * dir.x) + Mathf.Abs(b.extents.y * dir.y) + Mathf.Abs(b.extents.z * dir.z);
    }

    Vector3 ClosestCardinalDirection(Vector3 v)
    {
        float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
        if (ax > ay && ax > az) return v.x > 0 ? Vector3.right : Vector3.left;
        if (ay > az) return v.y > 0 ? Vector3.up : Vector3.down;
        return v.z > 0 ? Vector3.forward : Vector3.back;
    }

    string FormatDir(Vector3 d)
    {
        if (d == Vector3.right) return "+X";
        if (d == Vector3.left) return "-X";
        if (d == Vector3.up) return "+Y";
        if (d == Vector3.down) return "-Y";
        if (d == Vector3.forward) return "+Z";
        if (d == Vector3.back) return "-Z";
        return d.ToString("F2");
    }

    Bounds GetBounds(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>()
                          .Where(r => !(r is ParticleSystemRenderer))
                          .ToArray();
        if (renderers.Length > 0)
        {
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);
            return b;
        }

        var cols = go.GetComponentsInChildren<Collider>();
        if (cols.Length > 0)
        {
            Bounds b = cols[0].bounds;
            for (int i = 1; i < cols.Length; i++)
                b.Encapsulate(cols[i].bounds);
            return b;
        }

        return new Bounds(go.transform.position, Vector3.zero);
    }
}

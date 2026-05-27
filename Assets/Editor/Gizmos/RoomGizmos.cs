using BBI.Unity.Game;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class RoomGizmos
{
    static RoomGizmos()
    {
        SceneView.duringSceneGui += OnSceneGUI;
    }

    static void OnSceneGUI(SceneView sv)
    {
        if (GameRenderWindow.drawRooms)
        {
            foreach (var room in AddressableRendering.rooms)
            {
                if (room.parent == null || !room.parent.gameObject.activeInHierarchy) continue;
                if (!room.parent.TryGetComponent<RoomSubVolumeDefinition>(out var rvd)) continue;

                var m = Matrix4x4.TRS(room.parent.position, room.parent.rotation, room.parent.lossyScale)
                      * Matrix4x4.TRS(rvd.Center, Quaternion.identity, rvd.Size);
                DrawWireCubeFilled(m, rvd.Mode == RoomSubVolumeDefinition.InclusionMode.Include
                    ? GameRenderWindow.roomColorInclude
                    : GameRenderWindow.roomColorExclude);
            }
        }

        if (GameRenderWindow.drawRoomOverlaps)
        {
            foreach (var room in AddressableRendering.roomOverlaps)
            {
                if (room.parent == null || !room.parent.gameObject.activeInHierarchy) continue;
                if (!room.parent.TryGetComponent<RoomOpeningDefinition>(out var rod)) continue;

                var baseM = Matrix4x4.TRS(room.parent.position, room.parent.rotation, room.parent.lossyScale)
                           * Matrix4x4.TRS(rod.Center, Quaternion.identity, rod.Size);
                float b = 0.02f;
                DrawWireCubeFilled(baseM * Matrix4x4.TRS(new Vector3(-.5f, 0, 0), Quaternion.identity, new Vector3(b, 1, 1)), GameRenderWindow.roomOverlapColor);
                DrawWireCubeFilled(baseM * Matrix4x4.TRS(new Vector3( .5f, 0, 0), Quaternion.identity, new Vector3(b, 1, 1)), GameRenderWindow.roomOverlapColor);
                DrawWireCubeFilled(baseM * Matrix4x4.TRS(new Vector3(0, 0, -.5f), Quaternion.identity, new Vector3(1, 1, b)), GameRenderWindow.roomOverlapColor);
                DrawWireCubeFilled(baseM * Matrix4x4.TRS(new Vector3(0, 0,  .5f), Quaternion.identity, new Vector3(1, 1, b)), GameRenderWindow.roomOverlapColor);

                if (GameRenderWindow.drawRoomOverlapFlows)
                {
                    var origin = baseM.MultiplyPoint3x4(Vector3.zero);
                    Handles.color = GameRenderWindow.roomOverlapFlowColor;
                    var fwd = rod.FlowAxis == 1 ? Vector3.up : Vector3.forward;
                    DrawArrow.ForHandles(origin, fwd);
                    DrawArrow.ForHandles(origin, -fwd);
                }
            }
        }

        if (GameRenderWindow.drawJoints)
        {
            foreach (var jd in AddressableRendering.jointData)
            {
                var sz = jd.bounds.size;
                sz.x = Mathf.Max(sz.x, 0.05f);
                sz.y = Mathf.Max(sz.y, 0.05f);
                sz.z = Mathf.Max(sz.z, 0.05f);
                var m = jd.worldMatrix * Matrix4x4.TRS(jd.bounds.center, Quaternion.identity, sz);
                var col = jd.type == AddressableRendering.JointGizmoData.JointType.Root     ? GameRenderWindow.jointRootColor
                        : jd.type == AddressableRendering.JointGizmoData.JointType.CutPoint ? GameRenderWindow.jointCutColor
                        : GameRenderWindow.jointStandardColor;
                DrawWireOnly(m, col);
            }
        }

        if (GameRenderWindow.drawBakedJoints)
        {
            foreach (var jd in AddressableRendering.bakedJointData)
            {
                var sz = jd.bounds.size;
                sz.x = Mathf.Max(sz.x, 0.05f);
                sz.y = Mathf.Max(sz.y, 0.05f);
                sz.z = Mathf.Max(sz.z, 0.05f);
                var m = jd.worldMatrix * Matrix4x4.TRS(jd.bounds.center, Quaternion.identity, sz);
                DrawWireCubeFilled(m, GameRenderWindow.bakedJointColor);
            }
        }
    }

    // Filled cube (face alpha) + wire outline — replaces Gizmos.DrawCube
    static void DrawWireCubeFilled(Matrix4x4 m, Color col)
    {
        using (new Handles.DrawingScope(col, m))
        {
            // filled faces
            var fc = new Color(col.r, col.g, col.b, col.a * 0.35f);
            Handles.color = fc;
            DrawCubeFaces();
            // wire
            Handles.color = new Color(col.r, col.g, col.b, Mathf.Min(col.a * 1.5f, 1f));
            Handles.DrawWireCube(Vector3.zero, Vector3.one);
        }
    }

    // Wire-only cube — replaces Gizmos.DrawWireCube
    static void DrawWireOnly(Matrix4x4 m, Color col)
    {
        using (new Handles.DrawingScope(col, m))
            Handles.DrawWireCube(Vector3.zero, Vector3.one);
    }

    static readonly Vector3[] s_cubeFaceVerts = new Vector3[4];

    static void DrawCubeFaces()
    {
        DrawFace(new Vector3(-0.5f, 0, 0), Vector3.up, Vector3.forward);
        DrawFace(new Vector3( 0.5f, 0, 0), Vector3.up, Vector3.forward);
        DrawFace(new Vector3(0, -0.5f, 0), Vector3.right, Vector3.forward);
        DrawFace(new Vector3(0,  0.5f, 0), Vector3.right, Vector3.forward);
        DrawFace(new Vector3(0, 0, -0.5f), Vector3.right, Vector3.up);
        DrawFace(new Vector3(0, 0,  0.5f), Vector3.right, Vector3.up);
    }

    static void DrawFace(Vector3 center, Vector3 uAxis, Vector3 vAxis)
    {
        s_cubeFaceVerts[0] = center + (-uAxis - vAxis) * 0.5f;
        s_cubeFaceVerts[1] = center + ( uAxis - vAxis) * 0.5f;
        s_cubeFaceVerts[2] = center + ( uAxis + vAxis) * 0.5f;
        s_cubeFaceVerts[3] = center + (-uAxis + vAxis) * 0.5f;
        Handles.DrawSolidRectangleWithOutline(s_cubeFaceVerts, Handles.color, Color.clear);
    }
}

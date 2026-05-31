using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Diagnostic: confirms whether a sliced (or any) part has its scale BAKED into mesh geometry the
/// way the game's joint/mass system reads it. The game reads MeshFilter.sharedMesh raw vertices and
/// rejects non-unit localScale, so "baked" means: every transform in the chain is (1,1,1) AND the
/// mesh's own local-space vertex bounds already carry the stretch.
///
/// Select the part (e.g. PRF_..._Sliced) and run Shipbreaker/Shipbuilder Tools/Verify Slice Scale.
/// </summary>
public static class SliceScaleVerifier
{
    [MenuItem("Shipbreaker/Shipbuilder Tools/Verify Slice Scale", priority = 11)]
    static void Verify()
    {
        var sel = Selection.activeGameObject;
        if (sel == null) { Debug.LogError("[VerifyScale] Select a part in the scene first."); return; }

        var sb = new StringBuilder();
        sb.AppendLine($"[VerifyScale] Root '{sel.name}'");
        sb.AppendLine($"  root localScale={F(sel.transform.localScale)} lossyScale={F(sel.transform.lossyScale)}");

        bool anyNonUnit = false;
        Bounds? worldAll = null;

        foreach (var mf in sel.GetComponentsInChildren<MeshFilter>())
        {
            var mesh = mf.sharedMesh;
            if (mesh == null) continue;

            var t = mf.transform;
            // Local mesh bounds = the raw vertex extents the game reads (independent of any transform).
            Bounds local = mesh.bounds;
            // World bounds = how it actually appears = what joint colliders are generated against.
            Bounds world = TransformBounds(t.localToWorldMatrix, local);
            worldAll = worldAll == null ? world : Encapsulate(worldAll.Value, world);

            bool lossyUnit = Approx(t.lossyScale, Vector3.one);
            bool localUnit = Approx(t.localScale, Vector3.one);
            if (!lossyUnit) anyNonUnit = true;

            sb.AppendLine(
                $"  • {mf.name}: localScale={F(t.localScale)}{(localUnit ? "" : "  <-- NON-UNIT localScale")} " +
                $"lossyScale={F(t.lossyScale)}{(lossyUnit ? "" : "  <-- NON-UNIT lossyScale (game REJECTS)")}");
            sb.AppendLine(
                $"      meshLocalBoundsSize={F(local.size)}  ->  worldBoundsSize={F(world.size)}  readable={mesh.isReadable}");
        }

        if (worldAll != null)
            sb.AppendLine($"  TOTAL worldBoundsSize={F(worldAll.Value.size)}");

        sb.AppendLine(anyNonUnit
            ? "  RESULT: ✗ At least one mesh has NON-UNIT lossyScale — the game's joint/mass system will reject or misread it. Scale is NOT fully baked into geometry."
            : "  RESULT: ✓ All meshes at unit lossyScale. Scale lives in the mesh vertices (correctly baked). If parts still don't joint, the cause is NOT scale.");

        Debug.Log(sb.ToString());
    }

    static Bounds TransformBounds(Matrix4x4 m, Bounds b)
    {
        var c = m.MultiplyPoint3x4(b.center);
        var e = b.extents;
        var ax = m.MultiplyVector(new Vector3(e.x, 0, 0));
        var ay = m.MultiplyVector(new Vector3(0, e.y, 0));
        var az = m.MultiplyVector(new Vector3(0, 0, e.z));
        var ext = new Vector3(
            Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x),
            Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y),
            Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
        return new Bounds(c, ext * 2f);
    }

    static Bounds Encapsulate(Bounds a, Bounds b) { a.Encapsulate(b); return a; }
    static bool Approx(Vector3 v, Vector3 t) => Mathf.Abs(v.x - t.x) < 1e-4f && Mathf.Abs(v.y - t.y) < 1e-4f && Mathf.Abs(v.z - t.z) < 1e-4f;
    static string F(Vector3 v) => $"({v.x:F3}, {v.y:F3}, {v.z:F3})";
}

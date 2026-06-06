#if UNITY_EDITOR

using System.Collections;
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine;

public class CustomStage : PreviewSceneStage
{
    public static GameObject go;

    protected override GUIContent CreateHeaderContent()
    {
        return new GUIContent(go != null ? go.name : "Preview");
    }

    protected override void OnCloseStage()
    {
        // Destroy all objects in the preview scene so nothing leaks between previews.
        foreach (var root in scene.GetRootGameObjects())
            DestroyImmediate(root);
        base.OnCloseStage();
    }

    protected override bool OnOpenStage()
    {
        base.OnOpenStage();

        if(go == null)
            return false;

        // Add a simple directional light directly into the preview scene.
        var lightGo = new GameObject("PreviewLight");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        light.transform.rotation = Quaternion.Euler(45f, 45f, 0f);
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(lightGo, scene);

        var customStageGoInstance = Instantiate(go, Vector3.zero, Quaternion.identity);

        // Replace materials so they render correctly
        var fakeShader = Shader.Find("Fake/_Lynx/Surface/HDRP/Lit");
        var replacementMaterialCache = new Dictionary<Material, Material>();

        var defaultMat = new Material(fakeShader != null ? fakeShader : Shader.Find("Standard"));
        foreach(var renderer in customStageGoInstance.GetComponentsInChildren<MeshRenderer>())
        {
            var mats = (Material[])renderer.sharedMaterials.Clone();
            for(var i = 0; i < mats.Length; i++)
            {
                if(mats[i] == null)
                {
                    mats[i] = defaultMat;
                    continue;
                }

                if(replacementMaterialCache.ContainsKey(mats[i]))
                {
                    mats[i] = replacementMaterialCache[mats[i]];
                }
                else if(mats[i].shader?.name == "_Lynx/Surface/HDRP/Lit")
                {
                    var mat = new Material(fakeShader);
                    mat.CopyPropertiesFromMaterial(mats[i]);
                    mats[i] = mat;
                }
            }
            renderer.sharedMaterials = mats;
        }

        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(customStageGoInstance, scene);

        return true;
    }
}

#endif
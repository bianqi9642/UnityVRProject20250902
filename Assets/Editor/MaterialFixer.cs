using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using System.IO;

public class MaterialFixer : EditorWindow
{
    private DefaultAsset targetFolder;

    [MenuItem("Tools/Material Fixer")]
    public static void ShowWindow()
    {
        GetWindow<MaterialFixer>("Material Fixer");
    }

    void OnGUI()
    {
        GUILayout.Label("Batch Fix Materials for Prefabs", EditorStyles.boldLabel);
        targetFolder = (DefaultAsset)EditorGUILayout.ObjectField("Target Folder", targetFolder, typeof(DefaultAsset), false);

        if (GUILayout.Button("Fix Materials"))
        {
            if (targetFolder != null)
            {
                string path = AssetDatabase.GetAssetPath(targetFolder);
                FixMaterialsInFolder(path);
            }
            else
            {
                Debug.LogError("Please select a folder first!");
            }
        }
    }

    /// <summary>
    /// Detect current render pipeline (Built-in / URP / HDRP)
    /// </summary>
    static string GetPipelineShaderName()
    {
        var pipeline = GraphicsSettings.currentRenderPipeline;

        if (pipeline == null)
            return "Standard"; // Built-in RP

        string pipelineType = pipeline.GetType().ToString();

        if (pipelineType.Contains("UniversalRenderPipelineAsset"))
            return "Universal Render Pipeline/Lit"; // URP

        if (pipelineType.Contains("HDRenderPipelineAsset"))
            return "HDRP/Lit"; // HDRP

        return "Standard"; // fallback
    }

    /// <summary>
    /// Process all prefabs/materials inside the selected folder
    /// </summary>
    static void FixMaterialsInFolder(string folderPath)
    {
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { folderPath });

        foreach (string guid in prefabGuids)
        {
            string prefabPath = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab != null)
            {
                FixPrefabMaterials(prefab, folderPath);
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("✅ Material fixing complete for all prefabs!");
    }

    /// <summary>
    /// Fix all materials inside a prefab by creating independent copies
    /// </summary>
    static void FixPrefabMaterials(GameObject prefab, string folderPath)
    {
        Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>();

        foreach (Renderer rend in renderers)
        {
            Material[] mats = rend.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null)
                    continue;

                // Create a new independent material
                Material newMat = new Material(mats[i]);
                newMat.name = mats[i].name + "_Fixed";

                // Assign proper shader
                string shaderName = GetPipelineShaderName();
                Shader shader = Shader.Find(shaderName);
                if (shader != null)
                    newMat.shader = shader;

                // Try to find a matching texture by name
                string matName = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(mats[i]));
                string[] texGuids = AssetDatabase.FindAssets(matName + " t:Texture");
                if (texGuids.Length > 0)
                {
                    Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(texGuids[0]));
                    if (tex != null)
                    {
                        if (shaderName.Contains("Universal") || shaderName.Contains("HDRP"))
                            newMat.SetTexture("_BaseMap", tex);
                        else
                            newMat.SetTexture("_MainTex", tex);
                    }
                }

                // Save the new material asset
                string matPath = Path.Combine(folderPath, newMat.name + ".mat");
                matPath = AssetDatabase.GenerateUniqueAssetPath(matPath);
                AssetDatabase.CreateAsset(newMat, matPath);

                // Assign to renderer
                mats[i] = newMat;
            }

            rend.sharedMaterials = mats;
            EditorUtility.SetDirty(rend);
        }
    }
}

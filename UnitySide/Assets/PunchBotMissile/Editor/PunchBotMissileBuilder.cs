using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class PunchBotMissileBuilder
{
    [Serializable]
    private sealed class ModelData
    {
        public PartData[] parts;
        public MaterialData[] materials;
    }

    [Serializable]
    private sealed class PartData
    {
        public string name;
        public int material;
        public float[] vertices;
        public int[] triangles;
    }

    [Serializable]
    private sealed class MaterialData
    {
        public string name;
        public float[] color;
    }

    [MenuItem("Tools/PunchBot/Create Missile Prefab")]
    public static void Build()
    {
        string source = FindSource();
        ModelData data = JsonUtility.FromJson<ModelData>(File.ReadAllText(source));
        string pipeline = GraphicsSettings.currentRenderPipeline == null ? "" : GraphicsSettings.currentRenderPipeline.GetType().Name;
        string shaderName = pipeline.Contains("HDRenderPipeline") ? "HDRP/Lit" : (pipeline.Length == 0 ? "Standard" : "Universal Render Pipeline/Lit");
        Shader shader = Shader.Find(shaderName);
        if (shader == null) throw new InvalidOperationException("Material shader was not found: " + shaderName);

        string folder = AssetDatabase.GenerateUniqueAssetPath("Assets/PunchBotMissileGenerated");
        AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
        GameObject root = new GameObject("PunchBotMissile");

        try
        {
            Material[] materials = new Material[data.materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                MaterialData materialData = data.materials[i];
                Material material = new Material(shader) { name = materialData.name };
                Color color = new Color(materialData.color[0], materialData.color[1], materialData.color[2]);
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                if (material.HasProperty("_Color")) material.SetColor("_Color", color);
                if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0.25f);
                if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.3f);
                AssetDatabase.CreateAsset(material, folder + "/" + material.name + ".mat");
                materials[i] = material;
            }

            foreach (PartData part in data.parts)
            {
                Vector3[] vertices = new Vector3[part.vertices.Length / 3];
                for (int i = 0; i < vertices.Length; i++)
                {
                    int index = i * 3;
                    vertices[i] = new Vector3(part.vertices[index], part.vertices[index + 1], part.vertices[index + 2]);
                }
                Mesh mesh = new Mesh { name = part.name, vertices = vertices, triangles = part.triangles };
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                AssetDatabase.CreateAsset(mesh, folder + "/" + mesh.name + ".asset");
                GameObject child = new GameObject(part.name);
                child.transform.SetParent(root.transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = mesh;
                child.AddComponent<MeshRenderer>().sharedMaterial = materials[part.material];
            }

            GameObject exhaust = new GameObject("ExhaustPoint");
            exhaust.transform.SetParent(root.transform, false);
            exhaust.transform.localPosition = new Vector3(0f, 0f, -0.309f);
            exhaust.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            GameObject tip = new GameObject("TipPoint");
            tip.transform.SetParent(root.transform, false);
            tip.transform.localPosition = new Vector3(0f, 0f, 0.325f);

            string prefabPath = folder + "/PunchBotMissile.prefab";
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            AssetDatabase.SaveAssets();
            Selection.activeObject = prefab;
            EditorGUIUtility.PingObject(prefab);
            Debug.Log("Missile prefab created: " + prefabPath);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static string FindSource()
    {
        foreach (string guid in AssetDatabase.FindAssets("MissileModelData"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileName(path) == "MissileModelData.json") return path;
        }
        throw new FileNotFoundException("MissileModelData.json was not found inside Assets.");
    }
}

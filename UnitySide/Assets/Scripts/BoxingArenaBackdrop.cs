using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class BoxingArenaBackdrop : MonoBehaviour
{
    private readonly Dictionary<Material, List<CombineInstance>> batches = new Dictionary<Material, List<CombineInstance>>();
    private readonly List<Mesh> meshes = new List<Mesh>();
    private readonly List<Material> materials = new List<Material>();
    private Mesh cube;
    private Mesh sphere;

    public static void Install(PlayerController player)
    {
        if (FindFirstObjectByType<BoxingArenaBackdrop>() != null) return;
        GameObject arena = new GameObject("BoxingArenaBackdrop");
        GameObject ring = GameObject.Find("BoxingRing");
        if (ring != null) arena.transform.position = ring.transform.position;
        try
        {
            arena.AddComponent<BoxingArenaBackdrop>().Build();
        }
        catch (System.Exception exception)
        {
            Debug.LogException(exception, arena);
            Destroy(arena);
            return;
        }
        foreach (Camera camera in player.GetComponentsInChildren<Camera>(true))
        {
            if (camera.targetDisplay > 2) continue;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.008f, 0.012f, 0.025f);
        }
    }

    private void Build()
    {
        cube = GetPrimitiveMesh(PrimitiveType.Cube);
        sphere = GetPrimitiveMesh(PrimitiveType.Sphere);
        Material wall = CreateMaterial("ArenaNavy", new Color(0.018f, 0.025f, 0.045f));
        Material floor = CreateMaterial("ArenaFloor", new Color(0.035f, 0.04f, 0.055f));
        Material steel = CreateMaterial("LightingTruss", new Color(0.15f, 0.17f, 0.21f));
        Material red = CreateMaterial("RedSeats", new Color(0.23f, 0.035f, 0.045f));
        Material blue = CreateMaterial("BlueSeats", new Color(0.035f, 0.07f, 0.17f));
        Material crowd = CreateMaterial("CrowdSilhouettes", new Color(0.055f, 0.065f, 0.085f));
        Material heads = CreateMaterial("AudienceHeads", new Color(0.21f, 0.17f, 0.15f));
        Material lamps = CreateMaterial("SpotlightLenses", new Color(1f, 0.9f, 0.7f), true);
        Material redLed = CreateMaterial("RedAccent", new Color(0.5f, 0.025f, 0.035f), true);
        Material blueLed = CreateMaterial("BlueAccent", new Color(0.025f, 0.15f, 0.5f), true);

        Part(cube, floor, new Vector3(0f, -0.73f, 0f), new Vector3(24f, 0.1f, 24f), Quaternion.identity);
        Part(cube, wall, new Vector3(0f, 8.5f, 0f), new Vector3(24f, 0.3f, 24f), Quaternion.identity);
        for (int side = 0; side < 4; side++)
        {
            Quaternion rotation = Quaternion.Euler(0f, side * 90f, 0f);
            Material seat = side % 2 == 0 ? red : blue;
            Material accent = side % 2 == 0 ? redLed : blueLed;
            Part(cube, wall, rotation * new Vector3(0f, 3.7f, 11f), new Vector3(22f, 9f, 0.3f), rotation);
            Part(cube, accent, rotation * new Vector3(0f, 3.65f, 10.78f), new Vector3(16f, 0.045f, 0.08f), rotation);
            Part(cube, seat, rotation * new Vector3(0f, 4.8f, 10.75f), new Vector3(8f, 1.4f, 0.12f), rotation);
            for (int row = 0; row < 4; row++)
            {
                float z = 5.3f + row * 0.9f;
                float top = -0.35f + row * 0.5f;
                Part(cube, wall, rotation * new Vector3(0f, top - 0.25f, z), new Vector3(10.4f, 0.5f, 0.9f), rotation);
                for (int column = -5; column <= 5; column++)
                {
                    float x = column * 0.9f + (row % 2) * 0.18f;
                    Part(cube, seat, rotation * new Vector3(x, top + 0.16f, z), new Vector3(0.65f, 0.15f, 0.65f), rotation);
                    Part(cube, seat, rotation * new Vector3(x, top + 0.46f, z + 0.25f), new Vector3(0.65f, 0.55f, 0.12f), rotation);
                    float height = 0.92f + ((column + row + 12) % 3) * 0.06f;
                    Part(cube, crowd, rotation * new Vector3(x, top + 0.55f, z), new Vector3(0.38f, 0.58f, 0.26f), rotation);
                    Part(sphere, heads, rotation * new Vector3(x, top + height, z), Vector3.one * 0.25f, rotation);
                }
            }
            Part(cube, steel, rotation * new Vector3(0f, 6.15f, 3.9f), new Vector3(8.2f, 0.16f, 0.16f), rotation);
            Part(cube, steel, rotation * new Vector3(0f, 6.5f, 3.9f), new Vector3(8.2f, 0.12f, 0.12f), rotation);
            for (int column = -4; column <= 4; column++)
            {
                Part(cube, steel, rotation * new Vector3(column, 6.32f, 3.9f), new Vector3(0.08f, 0.4f, 0.08f), rotation * Quaternion.Euler(0f, 0f, 35f));
            }
            for (int lamp = -1; lamp <= 1; lamp += 2)
            {
                Vector3 position = rotation * new Vector3(lamp * 2.8f, 5.95f, 3.9f);
                Quaternion aim = Quaternion.LookRotation(new Vector3(0f, 0.6f, 0f) - position);
                Part(cube, steel, position, new Vector3(0.45f, 0.35f, 0.5f), aim);
                Part(cube, lamps, position + aim * Vector3.forward * 0.26f, new Vector3(0.32f, 0.24f, 0.035f), aim);
            }
        }
        foreach (var batch in batches)
        {
            Mesh mesh = new Mesh { name = batch.Key.name, indexFormat = IndexFormat.UInt32 };
            mesh.CombineMeshes(batch.Value.ToArray(), true, true);
            meshes.Add(mesh);
            GameObject section = new GameObject(batch.Key.name, typeof(MeshFilter), typeof(MeshRenderer));
            section.transform.SetParent(transform, false);
            section.GetComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = section.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = batch.Key;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
        batches.Clear();
        for (int x = -1; x <= 1; x += 2)
        for (int z = -1; z <= 1; z += 2)
        {
            GameObject fixture = new GameObject("RingSpotlight", typeof(Light));
            fixture.transform.SetParent(transform, false);
            fixture.transform.localPosition = new Vector3(x * 3.3f, 5.8f, z * 3.3f);
            fixture.transform.LookAt(transform.position + Vector3.up * 0.6f);
            Light light = fixture.GetComponent<Light>();
            light.type = LightType.Spot;
            light.color = new Color(1f, 0.94f, 0.83f);
            light.intensity = 1.3f;
            light.range = 14f;
            light.spotAngle = 65f;
            light.shadows = LightShadows.None;
        }
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.18f, 0.2f, 0.27f);
    }

    private Material CreateMaterial(string name, Color color, bool luminous = false)
    {
        Material template = Resources.Load<Material>(luminous ? "FightArena/ArenaUnlit" : "FightArena/ArenaLit");
        Material material;
        if (template != null)
        {
            material = new Material(template) { name = name };
        }
        else
        {
            Shader shader = Shader.Find(luminous ? "Universal Render Pipeline/Unlit" : "Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find(luminous ? "Unlit/Color" : "Standard");
            if (shader == null) throw new System.InvalidOperationException("背景素材がありません。Assets/Resources/FightArenaを含めてビルドしてください。");
            material = new Material(shader) { name = name };
        }
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        materials.Add(material);
        return material;
    }

    private Mesh GetPrimitiveMesh(PrimitiveType type)
    {
        GameObject template = GameObject.CreatePrimitive(type);
        template.SetActive(false);
        Mesh mesh = template.GetComponent<MeshFilter>().sharedMesh;
        Destroy(template);
        return mesh;
    }

    private void Part(Mesh mesh, Material material, Vector3 position, Vector3 scale, Quaternion rotation)
    {
        if (!batches.TryGetValue(material, out var pieces))
        {
            pieces = new List<CombineInstance>();
            batches.Add(material, pieces);
        }
        pieces.Add(new CombineInstance { mesh = mesh, transform = Matrix4x4.TRS(position, rotation, scale) });
    }

    private void OnDestroy()
    {
        foreach (Mesh mesh in meshes) if (mesh != null) Destroy(mesh);
        foreach (Material material in materials) if (material != null) Destroy(material);
    }
}

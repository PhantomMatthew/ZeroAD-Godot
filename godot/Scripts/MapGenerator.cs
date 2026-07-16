using Godot;
using System;

namespace ZeroAD.Godot;

public static class MapGenerator
{
    public sealed class GeneratedMap
    {
        public int PatchesPerSide;
        public float[,] Heightmap;
        public int VerticesPerSide;
        public float TileSize = 4.0f;

        public float GetHeight(int x, int z) =>
            x >= 0 && x < VerticesPerSide && z >= 0 && z < VerticesPerSide
                ? Heightmap[x, z] : 0;
    }

    public static GeneratedMap GenerateContinents(int patches, uint seed)
    {
        var rng = new Random((int)seed);
        int verts = patches * 16 + 1;
        var map = new GeneratedMap
        {
            PatchesPerSide = patches,
            VerticesPerSide = verts,
            Heightmap = new float[verts, verts]
        };

        float centerX = verts / 2f;
        float centerZ = verts / 2f;
        float maxRadius = verts * 0.45f;

        for (int z = 0; z < verts; z++)
        {
            for (int x = 0; x < verts; x++)
            {
                float dx = x - centerX;
                float dz = z - centerZ;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                float islandFalloff = Mathf.Clamp(1f - dist / maxRadius, 0f, 1f);

                float noise = 0;
                float freq = 0.05f;
                float amplitude = 8f;
                for (int oct = 0; oct < 4; oct++)
                {
                    float px = x * freq + seed * 0.001f;
                    float pz = z * freq + seed * 0.001f;
                    noise += Mathf.Sin(px * 7.3f) * Mathf.Cos(pz * 5.1f) * amplitude;
                    freq *= 2f;
                    amplitude *= 0.5f;
                }

                map.Heightmap[x, z] = islandFalloff * (3f + noise * 0.3f);
            }
        }

        return map;
    }

    public static GeneratedMap GenerateHighland(int patches, uint seed)
    {
        var rng = new Random((int)seed);
        int verts = patches * 16 + 1;
        var map = new GeneratedMap
        {
            PatchesPerSide = patches,
            VerticesPerSide = verts,
            Heightmap = new float[verts, verts]
        };

        for (int z = 0; z < verts; z++)
        {
            for (int x = 0; x < verts; x++)
            {
                float h = 5f;
                float freq = 0.03f;
                float amp = 6f;
                for (int oct = 0; oct < 5; oct++)
                {
                    float px = x * freq + seed * 0.01f;
                    float pz = z * freq + seed * 0.01f;
                    h += Mathf.Sin(px * 11.3f + pz * 7.7f) * amp;
                    freq *= 2f;
                    amp *= 0.5f;
                }
                map.Heightmap[x, z] = Mathf.Max(0, h);
            }
        }

        return map;
    }

    public static GeneratedMap GenerateFlat(int patches)
    {
        int verts = patches * 16 + 1;
        var map = new GeneratedMap
        {
            PatchesPerSide = patches,
            VerticesPerSide = verts,
            Heightmap = new float[verts, verts]
        };
        return map;
    }

    public static MeshInstance3D CreateMeshFromGenerated(GeneratedMap map)
    {
        int verts = map.VerticesPerSide;
        float ts = map.TileSize;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        for (int z = 0; z < verts; z++)
        {
            for (int x = 0; x < verts; x++)
            {
                float h = map.GetHeight(x, z);
                float t = Mathf.Clamp(h / 20f, 0f, 1f);
                st.SetColor(new Color(
                    0.2f + 0.5f * t,
                    0.4f + 0.3f * (1f - t),
                    0.15f + 0.2f * t));
                st.AddVertex(new Vector3(x * ts, h, z * ts));
            }
        }

        for (int z = 0; z < verts - 1; z++)
        {
            for (int x = 0; x < verts - 1; x++)
            {
                int i = z * verts + x;
                st.AddIndex(i);
                st.AddIndex(i + verts);
                st.AddIndex(i + 1);
                st.AddIndex(i + 1);
                st.AddIndex(i + verts);
                st.AddIndex(i + verts + 1);
            }
        }

        st.GenerateNormals();
        var mesh = st.Commit();

        var instance = new MeshInstance3D { Mesh = mesh };
        var mat = new StandardMaterial3D();
        mat.VertexColorUseAsAlbedo = true;
        mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        mesh.SurfaceSetMaterial(0, mat);
        instance.CreateTrimeshCollision();
        return instance;
    }
}

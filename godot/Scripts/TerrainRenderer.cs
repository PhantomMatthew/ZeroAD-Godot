using Godot;

namespace ZeroAD.Godot;

public static class TerrainRenderer
{
    public static MeshInstance3D CreateFromHeightmap(PmpMap map)
    {
        int verts = map.VerticesPerSide;
        float tileSize = PmpMap.TileSize;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        for (int z = 0; z < verts; z++)
        {
            for (int x = 0; x < verts; x++)
            {
                float h = map.GetHeight(x, z);

                float u = x * tileSize * 0.125f;
                float v = z * tileSize * 0.125f;
                st.SetUV(new Vector2(u, v));
                st.AddVertex(new Vector3(x * tileSize, h, z * tileSize));
            }
        }

        for (int z = 0; z < verts - 1; z++)
        {
            for (int x = 0; x < verts - 1; x++)
            {
                int i = z * verts + x;
                // 绕序修正:原顺序正面朝 −Y(GenerateNormals 随之烘出向下法线),镜像根下
                // 片元变正面 → FRONT_FACING 翻转不触发 → 地形法线恒 −Y 零太阳(发暗根因)。
                // 换成 Godot 惯例正面 +Y;镜像下背面光栅化,terrain_splat.gdshader 的
                // FRONT_FACING 翻转把 NORMAL 翻回 +Y。
                st.AddIndex(i);
                st.AddIndex(i + 1);
                st.AddIndex(i + verts);

                st.AddIndex(i + 1);
                st.AddIndex(i + verts + 1);
                st.AddIndex(i + verts);
            }
        }

        st.GenerateNormals();
        var mesh = st.Commit();

        // Splat material from the PMP's per-tile texture pairs (matches the original's
        // per-tile terrain texturing); single grass texture only as fallback.
        var splatMat = TerrainSplatBuilder.BuildMaterial(map);
        if (splatMat != null)
        {
            mesh.SurfaceSetMaterial(0, splatMat);
        }
        else
        {
            var mat = new StandardMaterial3D();
            var tex = LoadTexture("terrain_grass.png");
            if (tex != null)
            {
                mat.AlbedoTexture = tex;
            }
            else
            {
                mat.AlbedoColor = new Color(0.35f, 0.50f, 0.20f);
            }
            mat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            mesh.SurfaceSetMaterial(0, mat);
        }

        var instance = new MeshInstance3D { Mesh = mesh };
        instance.CreateTrimeshCollision();
        GD.Print($"Terrain mesh: {verts}x{verts}={verts*verts} verts, {(verts-1)*(verts-1)*2} tris");
        return instance;
    }

    public static MeshInstance3D CreateFlat(int patchesPerSide, float height = 0f)
    {
        int verts = patchesPerSide * PmpMap.PatchSize + 1;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        for (int z = 0; z < verts; z++)
        {
            for (int x = 0; x < verts; x++)
            {
                st.SetUV(new Vector2(x * 0.5f, z * 0.5f));
                st.AddVertex(new Vector3(x * PmpMap.TileSize, height, z * PmpMap.TileSize));
            }
        }

        for (int z = 0; z < verts - 1; z++)
        {
            for (int x = 0; x < verts - 1; x++)
            {
                int i = z * verts + x;
                // 绕序修正:原顺序正面朝 −Y(GenerateNormals 随之烘出向下法线),镜像根下
                // 片元变正面 → FRONT_FACING 翻转不触发 → 地形法线恒 −Y 零太阳(发暗根因)。
                // 换成 Godot 惯例正面 +Y;镜像下背面光栅化,terrain_splat.gdshader 的
                // FRONT_FACING 翻转把 NORMAL 翻回 +Y。
                st.AddIndex(i);
                st.AddIndex(i + 1);
                st.AddIndex(i + verts);

                st.AddIndex(i + 1);
                st.AddIndex(i + verts + 1);
                st.AddIndex(i + verts);
            }
        }

        st.GenerateNormals();
        var mesh = st.Commit();

        var mat = new StandardMaterial3D();
        var tex = LoadTexture("terrain_grass.png");
        if (tex != null)
            mat.AlbedoTexture = tex;
        else
            mat.AlbedoColor = new Color(0.4f, 0.6f, 0.25f);

        mesh.SurfaceSetMaterial(0, mat);
        return new MeshInstance3D { Mesh = mesh };
    }

    private static Texture2D? LoadTexture(string filename)
    {
        string path = ProjectSettings.GlobalizePath($"res://assets/textures/{filename}");
        if (!System.IO.File.Exists(path))
        {
            GD.PrintErr($"Texture not found: {path}");
            return null;
        }
        var img = Image.LoadFromFile(path);
        if (img == null)
        {
            GD.PrintErr($"Image.LoadFromFile failed: {path}");
            return null;
        }
        GD.Print($"Texture loaded: {filename} ({img.GetWidth()}x{img.GetHeight()})");
        return ImageTexture.CreateFromImage(img);
    }
}

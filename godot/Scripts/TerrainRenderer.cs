using Godot;

namespace ZeroAD.Godot;

public static class TerrainRenderer
{
    public static MeshInstance3D CreateFromHeightmap(PmpMap map)
    {
        // 早失败:VerticesPerSide 未赋值(适配器漏填)会静默建出 0 顶点空 mesh——
        // 地形不可见只剩天空色,极难排查。抛出让加载失败路径(回主菜单+日志)接管。
        if (map.VerticesPerSide < 2)
            throw new System.IO.InvalidDataException(
                $"PmpMap.VerticesPerSide={map.VerticesPerSide} (patches={map.PatchesPerSide}, " +
                $"heightmap={map.Heightmap.Length}) — adapter must set VerticesPerSide");
        int verts = map.VerticesPerSide;
        float tileSize = PmpMap.TileSize;
        float mapSize = map.MapSizeMeters;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        // 顶点直接建成世界坐标(z 预翻转):地形挂场景根而非镜像根(_worldRoot Scale.z=−1)。
        // 最小场景已证负 scale 对 StandardMaterial3D 在两个渲染器都无害(镜像/非镜像
        // 逐位一致),此举是架构简化而非修复:阴影直接自投(免镜像代理)、少一层负 scale
        // 表面、与已验证状态逐位等价(草地像素级一致)。UV 保持 sim 坐标不变
        // (fog/领土 overlay shader 与烘焙 albedo 的 Uv1Scale 均按 sim 世界采样)。
        for (int z = 0; z < verts; z++)
        {
            for (int x = 0; x < verts; x++)
            {
                float h = map.GetHeight(x, z);

                float u = x * tileSize * 0.125f;
                float v = z * tileSize * 0.125f;
                st.SetUV(new Vector2(u, v));
                st.AddVertex(new Vector3(x * tileSize, h, mapSize - z * tileSize));
            }
        }

        for (int z = 0; z < verts - 1; z++)
        {
            for (int x = 0; x < verts - 1; x++)
            {
                int i = z * verts + x;
                // z 预翻转是镜像变换(反转三角形手性),故用原始绕序即在世界上得到
                // 正面 +Y 法线(GenerateNormals 随之烘出向上法线)。
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

        // 引擎层防线:0-surface mesh 上 SurfaceSetMaterial 只触发引擎错误(不抛 C# 异常),
        // 结果是"地形隐形只剩天空色"且无任何托管堆栈。转为托管异常,让加载失败路径接管。
        if (mesh.GetSurfaceCount() == 0)
            throw new System.IO.InvalidDataException(
                $"terrain mesh has 0 surfaces (verts={verts}, heightmap={map.Heightmap.Length}, " +
                $"patches={map.PatchesPerSide})");

        // 烘焙 splat albedo → StandardMaterial3D:自定义 spatial shader 在 Compatibility
        // 完全收不到方向光阴影(渲染器限制),烘焙后走标准管线,受影/光照与 C++ 固定管线
        // 等价;雾/领土移到 fog_territory_overlay.gdshader 透明层(Main.SetupTerrain 挂)。
        var baked = SplatBaker.BakeAlbedo(map);
        if (baked != null)
        {
            float uvScale = 8f / map.MapSizeMeters; // 网格 UV=world×0.125 → 0..1 全图
            var mat = new StandardMaterial3D
            {
                AlbedoTexture = ImageTexture.CreateFromImage(baked),
                Uv1Scale = new Vector3(uvScale, uvScale, 1f),
                Roughness = 1f,
                SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            mesh.SurfaceSetMaterial(0, mat);
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
        ZeroAD.Sim.Diag.Log("Terrain", $"Terrain mesh: {verts}x{verts}={verts*verts} verts, {(verts-1)*(verts-1)*2} tris");
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
                // 绕序修正:正面朝 +Y(同 CreateFromHeightmap 的注释);fog_terrain.gdshader
                // 路径另有 FRONT_FACING 翻转兜底。
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
            ZeroAD.Sim.Diag.Err("Terrain", $"Texture not found: {path}");
            return null;
        }
        var img = Image.LoadFromFile(path);
        if (img == null)
        {
            ZeroAD.Sim.Diag.Err("Terrain", $"Image.LoadFromFile failed: {path}");
            return null;
        }
        ZeroAD.Sim.Diag.Log("Terrain", $"Texture loaded: {filename} ({img.GetWidth()}x{img.GetHeight()})");
        return ImageTexture.CreateFromImage(img);
    }
}

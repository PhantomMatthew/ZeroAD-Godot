using Godot;

namespace ZeroAD.Godot;

public static class TerrainRenderer
{
    /// <summary>建整图地形:按 64m patch(对齐 C++ PatchSize=16 tiles)分块烘焙贴图 + 分块
    /// mesh,避免"全图一张贴图"随地图增大被稀释(SplatBaker 类文档)。返回容器节点(含
    /// N×N 个 patch MeshInstance3D 子节点 + 1 个碰撞用子节点)与一份独立的整图 overlay
    /// mesh(仅 position/normal/UV,供 fog/territory 透明层复用,UV 约定与此前一致:
    /// world×0.125,不受 patch 分块影响)。</summary>
    public static (Node3D Root, Mesh OverlayMesh) CreateFromHeightmap(PmpMap map)
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
        var fullMesh = st.Commit();

        // 引擎层防线:0-surface mesh 上 SurfaceSetMaterial 只触发引擎错误(不抛 C# 异常),
        // 结果是"地形隐形只剩天空色"且无任何托管堆栈。转为托管异常,让加载失败路径接管。
        if (fullMesh.GetSurfaceCount() == 0)
            throw new System.IO.InvalidDataException(
                $"terrain mesh has 0 surfaces (verts={verts}, heightmap={map.Heightmap.Length}, " +
                $"patches={map.PatchesPerSide})");

        var root = new Node3D { Name = "Terrain" };

        // 烘焙 splat albedo → StandardMaterial3D:自定义 spatial shader 在 Compatibility
        // 完全收不到方向光阴影(渲染器限制),烘焙后走标准管线,受影/光照与 C++ 固定管线
        // 等价;雾/领土移到 fog_territory_overlay.gdshader 透明层(Main.SetupTerrain 挂)。
        var ctx = SplatBaker.PrepareBakeContext(map);
        // dev:A/B 开关——ZEROAD_TERRAIN_LEGACY=1 走无烘焙草地(排除贴图烘焙因素)。
        if (System.Environment.GetEnvironmentVariable("ZEROAD_TERRAIN_LEGACY") == "1") ctx = null;
        if (ctx != null)
        {
            // 生产路径:整图单张烘焙贴图(连续 UV world×0.125)。分块(每 64m 一张)
            // 会在地图边缘低角度把相邻 4m tile 行的贴图差异拉伸成清晰的"多层带"
            // (用户截图 + A/B 实证:整图连续 UV 无此现象);整图烘焙保留 C++ 风格的
            // 混合色,2048-8192px 密度足够。
            var bakedWhole = SplatBaker.BakeAlbedo(map);
            if (bakedWhole != null)
            {
                // Uv1Scale:网格 UV=world×0.125(1024m 图 → 0..128)压回 0..1,整图
                // 烘焙贴图与地形一一对应;漏了它整图贴图会平铺 128 次(贴图全错)。
                float uvScale = 8f / map.MapSizeMeters;
                var mat = new StandardMaterial3D
                {
                    AlbedoTexture = ImageTexture.CreateFromImage(bakedWhole),
                    Uv1Scale = new Vector3(uvScale, uvScale, 1f),
                    Roughness = 1f,
                    DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Lambert,
                    SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
                };
                fullMesh.SurfaceSetMaterial(0, mat);
                root.AddChild(new MeshInstance3D { Mesh = fullMesh, Name = "TerrainBaked" });
                ZeroAD.Sim.Diag.Log("Terrain",
                    $"Terrain mesh: {verts}x{verts}={verts * verts} verts, single baked albedo " +
                    $"({bakedWhole.GetWidth()}x{bakedWhole.GetHeight()}px, " +
                    $"{bakedWhole.GetWidth() / map.MapSizeMeters:F1} texel/m)");
            }
            bool flattened = bakedWhole != null;
            if (!flattened)
            {
                // 无 PMP 贴图数据/烘焙失败:整图一个 mesh(与此前行为一致)。
                var flatMat = new StandardMaterial3D
                {
                    DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Lambert,
                    SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                };
                var tex = LoadTexture("terrain_grass.png");
                if (tex != null)
                    flatMat.AlbedoTexture = tex;
                else
                    flatMat.AlbedoColor = new Color(0.35f, 0.50f, 0.20f);
                fullMesh.SurfaceSetMaterial(0, flatMat);
                root.AddChild(new MeshInstance3D { Mesh = fullMesh, Name = "TerrainFlat" });
                ZeroAD.Sim.Diag.Log("Terrain", $"Terrain mesh: {verts}x{verts}={verts * verts} verts, {(verts - 1) * (verts - 1) * 2} tris (no baked textures)");
            }
        }

        // 碰撞与可见 patch 解耦:整图只建一份(用今天同一套顶点/索引,不分块),避免 giant
        // 地图产生上千个独立 StaticBody3D。fullMesh 未挂任何可见 MeshInstance3D 时(patch
        // 分支)这里是它唯一的用途之一;不可见,只用来生成碰撞。
        var collisionCarrier = new MeshInstance3D { Mesh = fullMesh, Visible = false, Name = "TerrainCollision" };
        root.AddChild(collisionCarrier);
        collisionCarrier.CreateTrimeshCollision();

        return (root, fullMesh);
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

        var mat = new StandardMaterial3D
        {
            DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Lambert,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
        };
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

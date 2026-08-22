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
        if (ctx != null)
        {
            // 从整图 mesh 里取出已经算好的位置/法线(GenerateNormals 已跑过一次,逐位对齐
            // 今天的行为),按 patch 切片复用——相邻 patch 共享边界顶点用的是同一份数组里的
            // 同一个值,法线两侧完全一致,不会在 patch 缝上出现光照裂缝。
            var arrays = fullMesh.SurfaceGetArrays(0);
            var positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();

            int patchesPerSide = map.PatchesPerSide;
            int pxSize = SplatBaker.ComputePatchPixelSize(patchesPerSide);
            for (int pz = 0; pz < patchesPerSide; pz++)
            {
                for (int px = 0; px < patchesPerSide; px++)
                {
                    var patchMesh = BuildPatchMesh(positions, normals, verts, px, pz);
                    var baked = SplatBaker.BakeAlbedoPatch(ctx, px, pz, pxSize);
                    var mat = new StandardMaterial3D
                    {
                        AlbedoTexture = ImageTexture.CreateFromImage(baked),
                        Roughness = 1f,
                        DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Lambert,
                        SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                        // 各向异性:RTS 常见斜俯角下,patch 贴图在远处/斜角仍保持清晰
                        // (默认双线性+mipmap 在斜角会明显糊,此设置零显存代价)。
                        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                        // 必须 Clamp:patch 网格在共享边界把 UV 设成 1.0,默认 Repeat
                        // 会把 1.0 绕回 0.0,整条边采样到对面 64m 外的像素,最后一列
                        // tile 再把整张 patch 贴图斜插过去——地面就会变成整齐的方格。
                        TextureRepeat = false,
                    };
                    patchMesh.SurfaceSetMaterial(0, mat);
                    root.AddChild(new MeshInstance3D { Mesh = patchMesh, Name = $"Patch_{px}_{pz}" });
                }
            }
            ZeroAD.Sim.Diag.Log("Terrain",
                $"Terrain mesh: {verts}x{verts}={verts * verts} verts across {patchesPerSide * patchesPerSide} " +
                $"patches ({pxSize}x{pxSize}px/patch, {pxSize / (PmpMap.PatchSize * PmpMap.TileSize):F1} texel/m)");
        }
        else
        {
            // 无 PMP 贴图数据:不分块,整图一个 mesh(与此前行为一致)。
            var mat = new StandardMaterial3D
            {
                DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Lambert,
                SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            var tex = LoadTexture("terrain_grass.png");
            if (tex != null)
                mat.AlbedoTexture = tex;
            else
                mat.AlbedoColor = new Color(0.35f, 0.50f, 0.20f);
            fullMesh.SurfaceSetMaterial(0, mat);
            root.AddChild(new MeshInstance3D { Mesh = fullMesh, Name = "TerrainFlat" });
            ZeroAD.Sim.Diag.Log("Terrain", $"Terrain mesh: {verts}x{verts}={verts * verts} verts, {(verts - 1) * (verts - 1) * 2} tris (no patch textures)");
        }

        // 碰撞与可见 patch 解耦:整图只建一份(用今天同一套顶点/索引,不分块),避免 giant
        // 地图产生上千个独立 StaticBody3D。fullMesh 未挂任何可见 MeshInstance3D 时(patch
        // 分支)这里是它唯一的用途之一;不可见,只用来生成碰撞。
        var collisionCarrier = new MeshInstance3D { Mesh = fullMesh, Visible = false, Name = "TerrainCollision" };
        root.AddChild(collisionCarrier);
        collisionCarrier.CreateTrimeshCollision();

        return (root, fullMesh);
    }

    /// <summary>从整图位置/法线数组里切出一个 64m patch(17×17 顶点,含共享边界行列)的
    /// mesh,局部 UV 0..1 对应该 patch 自己的烘焙贴图(与 SplatBaker.BakeAlbedoPatch 的
    /// 像素→世界映射同一套公式:UV 分量 = patch 内 tile 分数)。边界顶点 UV 恰为 0 或 1,
    /// 材质必须 Clamp(见上方 TextureRepeat=false),否则 Repeat 把 1 绕成 0,排布错成方格。
    /// 不调用 GenerateNormals——法线直接取自整图已算好的值,保证与相邻 patch 逐位一致。</summary>
    private static ArrayMesh BuildPatchMesh(Vector3[] positions, Vector3[] normals, int verticesPerSide,
        int patchX, int patchZ)
    {
        int baseX = patchX * PmpMap.PatchSize;
        int baseZ = patchZ * PmpMap.PatchSize;
        const int n = PmpMap.PatchSize + 1; // 17 顶点/边(含共享边界)

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        for (int lz = 0; lz < n; lz++)
        {
            for (int lx = 0; lx < n; lx++)
            {
                int gi = (baseZ + lz) * verticesPerSide + (baseX + lx);
                st.SetNormal(normals[gi]);
                st.SetUV(new Vector2((float)lx / PmpMap.PatchSize, (float)lz / PmpMap.PatchSize));
                st.AddVertex(positions[gi]);
            }
        }

        for (int lz = 0; lz < n - 1; lz++)
        {
            for (int lx = 0; lx < n - 1; lx++)
            {
                int i = lz * n + lx;
                st.AddIndex(i);
                st.AddIndex(i + n);
                st.AddIndex(i + 1);

                st.AddIndex(i + 1);
                st.AddIndex(i + n);
                st.AddIndex(i + n + 1);
            }
        }

        return (ArrayMesh)st.Commit();
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

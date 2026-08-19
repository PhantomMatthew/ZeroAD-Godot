using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen
{
    /// <summary>地图生成常量（原版 library.js:21-22 + MapGenerator.cpp:123-126）。</summary>
    public static class RmgenConstants
    {
        public const double SEA_LEVEL = 20.0;
        public const double HEIGHT_UNITS_PER_METRE = 92;
        public const int TERRAIN_TILE_SIZE = 4;   // 引擎注入（source/graphics/Terrain.h）
        public const int MAP_BORDER_WIDTH = 3;    // 引擎注入（source MapEdgeTiles.h MAP_EDGE_TILES=3）
    }

    /// <summary>地图生成的中心数据结构（逐字移植 RandomMap.js，499 行）。
    /// 存储 heightmap（float32 精度）、texture grid、terrain entities、entities。
    /// makeExportable 输出 {entities, height, textureNames, tileData} 给引擎消费。</summary>
    public sealed class RandomMap
    {
        public int Size;
        public int EntityCount = 150;

        // 纹理名 ↔ ID 映射
        public readonly Dictionary<string, int> NameToID = new();
        public readonly List<string> IDToName = new();

        // 纹理网格 [x][z]（ushort = Uint16Array）
        public ushort[][] Texture;
        // 地形实体 [x][z]（null = 无）
        public RmgenEntity?[][] TerrainEntities;
        // 高度图 [x][z]（float = Float32Array，保真度关键）
        public float[][] Height;
        // 实体列表
        public readonly List<RmgenEntity> Entities = new();

        // RNG（由构造器传入，共享种子）
        private readonly RmgenRng _rng;
        private readonly bool _circularMap;

        public RandomMap(RmgenRng rng, int size, double baseHeight, string baseTerrain, bool circularMap = false)
        {
            _rng = rng;
            Size = size;
            _circularMap = circularMap;

            // 纹理网格
            Texture = new ushort[size][];
            for (int x = 0; x < size; x++)
            {
                Texture[x] = new ushort[size];
                for (int z = 0; z < size; z++)
                    Texture[x][z] = (ushort)GetTextureID(baseTerrain);
            }

            // 地形实体网格
            TerrainEntities = new RmgenEntity?[size][];
            for (int x = 0; x < size; x++)
                TerrainEntities[x] = new RmgenEntity?[size];

            // 高度图（corner-based: size+1，tile-centered: size）
            int mapSize = size + 1;  // 默认 TILE_CENTERED_HEIGHT_MAP = false
            Height = new float[mapSize][];
            for (int x = 0; x < mapSize; x++)
            {
                Height[x] = new float[mapSize];
                for (int z = 0; z < mapSize; z++)
                    Height[x][z] = (float)baseHeight;  // Float32 存储！
            }
        }

        /// <summary>基底贴图名单版构造器（上游 RandomMap(baseHeight, baseTerrain[])——
        /// 逐图块 pickRandom，size² 次抽数发生在生成最开始）。</summary>
        public RandomMap(RmgenRng rng, int size, double baseHeight, IReadOnlyList<string> baseTerrain,
            bool circularMap = false)
        {
            _rng = rng;
            Size = size;
            _circularMap = circularMap;

            Texture = new ushort[size][];
            for (int x = 0; x < size; x++)
            {
                Texture[x] = new ushort[size];
                for (int z = 0; z < size; z++)
                    Texture[x][z] = (ushort)GetTextureID(rng.PickRandom(baseTerrain));
            }

            TerrainEntities = new RmgenEntity?[size][];
            for (int x = 0; x < size; x++)
                TerrainEntities[x] = new RmgenEntity?[size];

            int mapSize = size + 1;
            Height = new float[mapSize][];
            for (int x = 0; x < mapSize; x++)
            {
                Height[x] = new float[mapSize];
                for (int z = 0; z < mapSize; z++)
                    Height[x][z] = (float)baseHeight;
            }
        }

        public int GetTextureID(string texture)
        {
            if (NameToID.TryGetValue(texture, out int id))
                return id;
            id = IDToName.Count;
            NameToID[texture] = id;
            IDToName.Add(texture);
            return id;
        }

        public int GetEntityID() => EntityCount++;
        public int GetSize() => Size;

        public bool IsCircularMap() => _circularMap;

        public RmgenVector2D GetCenter() => new(Size / 2.0, Size / 2.0);

        public bool ValidTile(RmgenVector2D pos, double distance = 0)
        {
            if (_circularMap)
                return SafeMath.Round(pos.DistanceTo(GetCenter())) < Size / 2.0 - distance - 1;
            return pos.X >= distance && pos.Y >= distance && pos.X < Size - distance && pos.Y < Size - distance;
        }

        public bool ValidTilePassable(RmgenVector2D pos, double distance = 0)
            => ValidTile(pos, distance + RmgenConstants.MAP_BORDER_WIDTH);

        public bool InMapBounds(RmgenVector2D pos)
            => pos.X >= 0 && pos.Y >= 0 && pos.X < Size && pos.Y < Size;

        public bool ValidHeight(RmgenVector2D pos)
        {
            if (pos.X < 0 || pos.Y < 0) return false;
            return pos.X <= Size && pos.Y <= Size;  // corner-based: <= Size
        }

        /// <summary>g_AdjacentCoordinates（math.js）——8 邻域偏移。</summary>
        private static readonly RmgenVector2D[] AdjacentCoordinates =
        {
            new(1, 0), new(1, 1), new(0, 1), new(-1, 1),
            new(-1, 0), new(-1, -1), new(0, -1), new(1, -1),
        };

        /// <summary>getAdjacentPoints——图内 8 邻域点（加偏移后 round）。</summary>
        public List<RmgenVector2D> GetAdjacentPoints(RmgenVector2D position)
        {
            var result = new List<RmgenVector2D>();
            foreach (var c in AdjacentCoordinates)
            {
                var p = RmgenVector2D.Add(position, c);
                p.Round();
                if (InMapBounds(p))
                    result.Add(p);
            }
            return result;
        }

        /// <summary>getSlope——相邻图块平均高度差（坡度）。</summary>
        public double GetSlope(RmgenVector2D position)
        {
            var adjacentPositions = GetAdjacentPoints(position);
            if (adjacentPositions.Count == 0)
                return 0;
            double totalSlope = 0;
            foreach (var adjacentPos in adjacentPositions)
                totalSlope += Math.Abs(GetHeight(adjacentPos) - GetHeight(position));
            return totalSlope / adjacentPositions.Count;
        }

        public string GetTexture(RmgenVector2D pos)
            => IDToName[Texture[(int)pos.X][(int)pos.Y]];

        public void SetTexture(RmgenVector2D pos, string textureName)
        {
            Texture[(int)pos.X][(int)pos.Y] = (ushort)GetTextureID(textureName);
        }

        public double GetHeight(RmgenVector2D pos)
            => Height[(int)pos.X][(int)pos.Y];  // 返回 float 存储值（double 提升）

        public void SetHeight(RmgenVector2D pos, double height)
        {
            Height[(int)pos.X][(int)pos.Y] = (float)height;  // Float32 截断！
        }

        public RmgenEntity PlaceEntityAnywhere(string templateName, int playerID, RmgenVector2D position, double orientation)
        {
            var entity = new RmgenEntity(GetEntityID(), templateName, playerID, position, orientation);
            Entities.Add(entity);
            return entity;
        }

        public RmgenEntity? PlaceEntityPassable(string templateName, int playerID, RmgenVector2D position, double orientation)
        {
            if (!ValidTilePassable(position)) return null;
            return PlaceEntityAnywhere(templateName, playerID, position, orientation);
        }

        public void SetTerrainEntity(string templateName, int playerID, RmgenVector2D position, double orientation)
        {
            int x = (int)SafeMath.Floor(position.X);
            int z = (int)SafeMath.Floor(position.Y);
            TerrainEntities[x][z] = new RmgenEntity(GetEntityID(), templateName, playerID, position, orientation);
        }

        public void DeleteTerrainEntity(RmgenVector2D position)
        {
            int x = (int)SafeMath.Floor(position.X);
            int z = (int)SafeMath.Floor(position.Y);
            TerrainEntities[x][z] = null;
        }

        /// <summary>导出高度数据（逐字移植 exportHeightData）。
        /// clamp(floor((height + 20) * 92), 0, 0xFFFF)。先 floor 再 clamp。</summary>
        public ushort[] ExportHeightData()
        {
            int hms = Size + 1;
            var heightmap = new ushort[hms * hms];
            for (int x = 0; x < hms; x++)
                for (int z = 0; z < hms; z++)
                {
                    double currentHeight = Height[x][z];  // float 存储值
                    int encoded = (int)SafeMath.Floor((currentHeight + RmgenConstants.SEA_LEVEL) * RmgenConstants.HEIGHT_UNITS_PER_METRE);
                    heightmap[z * hms + x] = (ushort)Math.Max(0, Math.Min(0xFFFF, encoded));
                }
            return heightmap;
        }

        /// <summary>导出地形纹理（逐字移植 exportTerrainTextures）。</summary>
        public (ushort[] index, ushort[] priority) ExportTerrainTextures()
        {
            var idx = new ushort[Size * Size];
            var pri = new ushort[Size * Size];
            for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++)
                {
                    idx[z * Size + x] = Texture[x][z];
                    pri[z * Size + x] = Texture[x][z];
                }
            return (idx, pri);
        }

        /// <summary>导出实体列表（逐字移植 exportEntityList）。
        /// 注意：rotation.y = PI/2 - rotation.y 变换。</summary>
        public List<RmgenEntity> ExportEntityList()
        {
            // 旋转变换：simple 2D → 3D
            foreach (var e in Entities)
                e.Orientation = SafeMath.PI / 2 - e.Orientation;

            // 追加地形实体
            for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++)
                    if (TerrainEntities[x][z] != null)
                        Entities.Add(TerrainEntities[x][z]!);
            return Entities;
        }

        /// <summary>导出全部数据（逐字移植 MakeExportable）。</summary>
        public MapExport MakeExportable()
        {
            var (idx, pri) = ExportTerrainTextures();
            return new MapExport
            {
                Entities = ExportEntityList(),
                Height = ExportHeightData(),
                SeaLevel = RmgenConstants.SEA_LEVEL,
                Size = Size,
                TextureNames = new List<string>(IDToName),
                TileIndex = idx,
                TilePriority = pri,
            };
        }
    }

    /// <summary>导出的地图数据（MakeExportable 的 C# POCO）。</summary>
    public sealed class MapExport
    {
        public List<RmgenEntity> Entities = new();
        public ushort[] Height = System.Array.Empty<ushort>();
        public double SeaLevel;
        public int Size;
        public List<string> TextureNames = new();
        public ushort[] TileIndex = System.Array.Empty<ushort>();
        public ushort[] TilePriority = System.Array.Empty<ushort>();
    }

    /// <summary>地图生成的实体（逐字移植 Entity.js）。</summary>
    public sealed class RmgenEntity
    {
        public readonly int Id;
        public readonly string TemplateName;
        public readonly int PlayerID;
        public readonly RmgenVector2D Position;
        public double Orientation;  // 可变（exportEntityList 变换 rotation.y）

        public RmgenEntity(int id, string templateName, int playerID, RmgenVector2D position, double orientation)
        {
            Id = id; TemplateName = templateName; PlayerID = playerID;
            Position = position; Orientation = orientation;
        }
    }
}

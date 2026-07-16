using System;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

[Component("Terrain", "Terrain")]
public sealed class TerrainComponent : ComponentBase, IComponentMessageHandler
{
    public int MapSize { get; private set; }
    public float TileSize { get; private set; }

    protected override void OnInit()
    {
        MapSize = 64;
        TileSize = 4.0f;
    }

    public void Configure(int mapSize, float tileSize)
    {
        MapSize = mapSize;
        TileSize = tileSize;
    }

    public float GetWorldSize() => MapSize * TileSize;

    public bool IsInBounds(FixedVector2D pos)
    {
        float x = pos.X.ToFloat();
        float y = pos.Y.ToFloat();
        return x >= 0 && x < GetWorldSize() && y >= 0 && y < GetWorldSize();
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("size", MapSize);
        s.NumberFixed("tile", Fixed.FromFloat(TileSize));
    }

    public override void Deserialize(IDeserializer d)
    {
        MapSize = d.NumberI32("size");
        TileSize = d.NumberFixed("tile").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}

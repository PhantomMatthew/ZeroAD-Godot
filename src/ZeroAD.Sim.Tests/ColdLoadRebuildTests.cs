using System;
using System.IO;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 冷加载(跨场景)空间索引重建——SimBridge.RebuildSpatialIndexesAfterLoad 的内核半边。
/// DeserializeSaveGame 直写组件字典、不触发 EntityCreated,故"新进程/新场景"那侧的
/// ObstructionManager shapes 与 RangeManager._data 都是空的(热加载靠旧实例同 id 残留掩盖了
/// 这一点)。钉死:反序列化后索引为空 → Repopulate + EnsureRegistered 后回填;玩家注册表随
/// v6 payload 往返(Players.Deserialize 先清残留再重指);整体状态 hash 一致。
/// </summary>
public sealed class ColdLoadRebuildTests
{
    /// <summary>建一个含 LOS 系统实体、双玩家、一个 obstruction 单位(player 1)与一个
    /// obstruction 建筑(player 2)的世界。输出关键实体 id 供冷侧按同 id 重查(存档保留 id)。</summary>
    private static ComponentManager BuildWorld(out RangeManager range, out EntityId unit, out EntityId building)
    {
        var cm = new ComponentManager(42);
        cm.Registry.AutoRegister(typeof(PositionComponent).Assembly);
        SimSystem.Init(cm);
        range = new RangeManager(cm, Fixed.FromInt(64), Fixed.FromInt(64));
        range.SetBounds(Fixed.FromInt(64));
        SimSystem.SetObstructionManager(new ObstructionManager(64, 4f));

        var sys = cm.CreateEntity();
        var los = new LosManagerComponent();
        cm.AddComponent(sys, los);
        los.Attach(range);

        foreach (var pid in new[] { 1, 2 })
        {
            var pe = cm.CreateEntity();
            cm.AddComponent(pe, new PlayerComponent());
            cm.AddComponent(pe, new OwnershipComponent { PlayerId = pid });
            cm.AddComponent(pe, new DiplomacyComponent());
            cm.Players.AddPlayer(pid, pe);
        }

        // obstruction-bearing unit (player 1), placed in-world.
        unit = cm.CreateEntity();
        cm.AddComponent(unit, new PositionComponent());
        cm.QueryInterface<PositionComponent>(unit)!.Position =
            new FixedVector3D(Fixed.FromInt(10), Fixed.Zero, Fixed.FromInt(10));
        cm.AddComponent(unit, new OwnershipComponent { PlayerId = 1 });
        cm.AddComponent(unit, new IdentityComponent());
        var unitObs = new ObstructionComponent { Type = ObstructionType.Unit, Size0 = Fixed.FromInt(1) };
        cm.AddComponent(unit, unitObs);
        unitObs.EnsureRegistered();

        // obstruction-bearing static building (player 2), placed in-world.
        building = cm.CreateEntity();
        cm.AddComponent(building, new PositionComponent());
        cm.QueryInterface<PositionComponent>(building)!.Position =
            new FixedVector3D(Fixed.FromInt(32), Fixed.Zero, Fixed.FromInt(32));
        cm.AddComponent(building, new OwnershipComponent { PlayerId = 2 });
        cm.AddComponent(building, new IdentityComponent());
        var bObs = new ObstructionComponent
        {
            Type = ObstructionType.Static,
            Size0 = Fixed.FromInt(8),
            Size1 = Fixed.FromInt(8),
        };
        cm.AddComponent(building, bObs);
        bObs.EnsureRegistered();

        return cm;
    }

    /// <summary>冷侧:全新 ComponentManager + RangeManager + ObstructionManager(等价新
    /// SimBridge 实例)。反序列化后断言索引为空,再走 Repopulate + EnsureRegistered 回填。</summary>
    [Fact]
    public void ColdLoad_IndexesEmptyAfterDeserialize_ThenRebuilds()
    {
        var cmA = BuildWorld(out _, out var unit, out var building);
        byte[] hashA = cmA.ComputeStateHash();
        var player1A = cmA.Players.GetPlayerEntityId(1);
        var player2A = cmA.Players.GetPlayerEntityId(2);

        var ms = new MemoryStream();
        cmA.SerializeSaveGame(new BinarySerializer(new BinaryWriter(ms)));
        ms.Position = 0;

        var cmB = new ComponentManager(42);
        cmB.Registry.AutoRegister(typeof(PositionComponent).Assembly);
        SimSystem.Init(cmB);
        var rangeB = new RangeManager(cmB, Fixed.FromInt(64), Fixed.FromInt(64));
        rangeB.SetBounds(Fixed.FromInt(64));
        var obstructionsB = new ObstructionManager(64, 4f);
        SimSystem.SetObstructionManager(obstructionsB);

        cmB.DeserializeSaveGame(new BinaryDeserializer(new BinaryReader(ms)), comp =>
        {
            if (comp is LosManagerComponent l) l.Attach(rangeB);
        });

        // --- 重建前:索引为空(bug 现场) ---
        // RangeManager._data 无任何在世的玩家实体(Deserialize 未触发 EntityCreated)。
        Assert.Empty(rangeB.GetEntitiesByPlayer(1));
        Assert.Empty(rangeB.GetEntitiesByPlayer(2));
        // ObstructionComponent 反序列化后不自带注册 → 新 ObstructionManager 里无 shape。
        var unitObsB = cmB.QueryInterface<ObstructionComponent>(unit)!;
        Assert.False(unitObsB.Tag.IsValid);

        // --- 重建(RebuildSpatialIndexesAfterLoad 的内核半边) ---
        foreach (var e in cmB.AllEntities)
            cmB.QueryInterface<ObstructionComponent>(e)?.EnsureRegistered();
        rangeB.Repopulate(cmB.AllEntities);

        // --- 重建后:索引回填 ---
        Assert.True(unitObsB.Tag.IsValid);
        Assert.NotNull(obstructionsB.GetObstruction(unitObsB.Tag));
        var bObsB = cmB.QueryInterface<ObstructionComponent>(building)!;
        Assert.True(bObsB.Tag.IsValid);
        Assert.NotNull(obstructionsB.GetObstruction(bObsB.Tag));
        Assert.Contains(unit, rangeB.GetEntitiesByPlayer(1));
        Assert.Contains(building, rangeB.GetEntitiesByPlayer(2));

        // --- 玩家注册表随 v6 payload 往返(先清残留再重指到存活实体) ---
        Assert.NotNull(cmB.Players.GetPlayerEntityId(1));
        Assert.Equal(player1A, cmB.Players.GetPlayerEntityId(1));
        Assert.Equal(player2A, cmB.Players.GetPlayerEntityId(2));
        Assert.NotNull(cmB.Players.GetPlayerEntity(1));

        // --- 整体状态 hash 一致 ---
        byte[] hashB = cmB.ComputeStateHash();
        Assert.True(hashA.AsSpan().SequenceEqual(hashB));
    }

    /// <summary>Repopulate 幂等:重复调用只在 _data 已缺时播种、只刷新,不重复入索引。
    /// 二次调用后玩家实体集与 obstruction 注册数保持不变。</summary>
    [Fact]
    public void Repopulate_IsIdempotent()
    {
        var cm = BuildWorld(out var range, out var unit, out _);

        // 世界经 CreateEntity 已由 EntityCreated 播种 _data;Repopulate 重跑只刷新、不翻倍。
        range.Repopulate(cm.AllEntities);
        var once = range.GetEntitiesByPlayer(1);
        range.Repopulate(cm.AllEntities);
        var twice = range.GetEntitiesByPlayer(1);

        Assert.Equal(once.Count, twice.Count);
        Assert.Contains(unit, twice);
    }
}

using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using Xunit;

namespace ZeroAD.Sim.Tests;

// BuildingAI — port of BuildingAI.js:自动索敌齐射、手动集火(unitAITarget/focusTargets)、
// focus-fire 命令路由、翻面清集火(易主全清/外交翻面只清 unitAITarget,WS3 裁定)、
// 逐箭射程+LOS 校验(打不中顺延)。
public sealed class BuildingAITests
{
    private static (ComponentManager cm, RangeManager rm) NewWorld()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var rm = new RangeManager(cm, Fixed.FromInt(256), Fixed.FromInt(256));
        SimSystem.SetRangeManager(rm);
        AddPlayer(cm, 1);
        AddPlayer(cm, 2);
        // 1/2 不同队 → 互敌(默认 Neutral 不开火)。
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 1 });
        rm.SetLosRevealAll(1, true);
        rm.UpdateVisibilityData();
        return (cm, rm);
    }

    private static void AddPlayer(ComponentManager cm, int playerId)
    {
        var pe = cm.CreateEntity();
        cm.AddComponent(pe, new PlayerComponent());
        cm.AddComponent(pe, new DiplomacyComponent());
        cm.Players.AddPlayer(playerId, pe);
    }

    private static void RegisterWithRange(ComponentManager cm, RangeManager rm, EntityId e, float x, float z)
    {
        cm.NotifyEntityCreated(e);
        rm.RefreshFromComponents(e);
        var p = new FixedVector2D(Fixed.FromFloat(x), Fixed.FromFloat(z));
        cm.NotifyPositionChanged(e, p, p);
    }

    /// <summary>防御建筑:Position + Attack(range/rate)+ Ownership + BuildingAI。</summary>
    private static EntityId MakeBuilding(ComponentManager cm, RangeManager rm,
        int player = 1, float x = 100f, float z = 100f, float range = 30f)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        var id = new IdentityComponent();
        cm.AddComponent(e, id);
        id.Classes.Add("Structure");
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        var atk = new AttackComponent { Damage = new DamageBlock(DamageType.Hack, 5) };
        cm.AddComponent(e, atk);
        atk.Range = range;
        atk.Rate = 1f;
        cm.AddComponent(e, new BuildingAIComponent { DefaultArrowCount = 1 });
        RegisterWithRange(cm, rm, e, x, z);
        return e;
    }

    private static EntityId MakeEnemyUnit(ComponentManager cm, RangeManager rm,
        float x, float z, int player = 2)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new UnitMotion());
        var id = new IdentityComponent();
        cm.AddComponent(e, id);
        id.Classes.Add("Unit");
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        var h = new HealthComponent();
        cm.AddComponent(e, h);
        h.Current = 100; h.Max = 100;      // OnInit 清空后赋值(防 clobber)
        RegisterWithRange(cm, rm, e, x, z);
        return e;
    }

    private static BuildingAIComponent Bai(ComponentManager cm, EntityId building) =>
        cm.QueryInterface<BuildingAIComponent>(building)!;

    /// <summary>喂一次扫描节拍:先刷新 LOS 缓存(实体注册后可见性要逐拍重估),
    /// 再 Tick 1s 触发节流刷新 _targets(期间可能齐射,测试不敏感)。</summary>
    private static void Scan(ComponentManager cm, RangeManager rm, EntityId building)
    {
        rm.UpdateVisibilityData();
        Bai(cm, building).Tick(1.0f, cm);
    }

    [Fact]
    public void AddFocusTarget_RequiresTargetInRange()
    {
        var (cm, rm) = NewWorld();
        var building = MakeBuilding(cm, rm);
        var near = MakeEnemyUnit(cm, rm, 110f, 100f);   // 射程 30 内
        var far = MakeEnemyUnit(cm, rm, 200f, 200f);    // 射程外
        Scan(cm, rm, building);
        var bai = Bai(cm, building);

        bai.AddFocusTarget(near, queued: false);
        Assert.Equal(new[] { near }, bai.FocusTargets);

        // 射程外目标忽略(原版 targetUnits 前提);非法 id 忽略。
        bai.AddFocusTarget(far, queued: false);
        Assert.Equal(new[] { near }, bai.FocusTargets);
        bai.AddFocusTarget(default, queued: false);
        Assert.Equal(new[] { near }, bai.FocusTargets);
    }

    [Fact]
    public void AddFocusTarget_QueuedTail_PushFrontHead_PlainReplaces()
    {
        var (cm, rm) = NewWorld();
        var building = MakeBuilding(cm, rm);
        var a = MakeEnemyUnit(cm, rm, 105f, 100f);
        var b = MakeEnemyUnit(cm, rm, 110f, 100f);
        var c = MakeEnemyUnit(cm, rm, 115f, 100f);
        Scan(cm, rm, building);
        var bai = Bai(cm, building);

        bai.AddFocusTarget(a, queued: false);                 // 覆盖单目标
        Assert.Equal(new[] { a }, bai.FocusTargets);
        bai.AddFocusTarget(b, queued: true);                  // 追加尾
        Assert.Equal(new[] { a, b }, bai.FocusTargets);
        bai.AddFocusTarget(c, queued: false, pushFront: true); // 头插
        Assert.Equal(new[] { c, a, b }, bai.FocusTargets);
        bai.AddFocusTarget(b, queued: false);                 // 覆盖
        Assert.Equal(new[] { b }, bai.FocusTargets);
    }

    [Fact]
    public void FocusFire_Executor_RoutesToBuildingAI_ValidatesOwnership()
    {
        var (cm, rm) = NewWorld();
        var building = MakeBuilding(cm, rm);
        var target = MakeEnemyUnit(cm, rm, 110f, 100f);
        Scan(cm, rm, building);
        var executor = new SimCommandExecutor(cm);

        // 非属主下令 → 忽略(同 Delete/Gate 的归属校验)。
        executor.Apply(NetCommand.FocusFire(2, building.Value, target.Value));
        Assert.Empty(Bai(cm, building).FocusTargets);

        executor.Apply(NetCommand.FocusFire(1, building.Value, target.Value));
        Assert.Equal(new[] { target }, Bai(cm, building).FocusTargets);

        // 射程外目标:命令到达但 AddFocusTarget 前提拒绝。
        var far = MakeEnemyUnit(cm, rm, 220f, 220f);
        executor.Apply(NetCommand.FocusFire(1, building.Value, far.Value));
        Assert.Equal(new[] { target }, Bai(cm, building).FocusTargets);
    }

    [Fact]
    public void OwnershipChange_ClearsFocusTargetsAndUnitAITarget()
    {
        var (cm, rm) = NewWorld();
        var building = MakeBuilding(cm, rm);
        var target = MakeEnemyUnit(cm, rm, 110f, 100f);
        Scan(cm, rm, building);   // 首个 Tick 懒订阅事件
        var bai = Bai(cm, building);
        bai.SetUnitAITarget(target);
        bai.AddFocusTarget(target, queued: false);

        // 易主(本建筑)→ 清 focusTargets + unitAITarget(WS3 裁定)。
        cm.QueryInterface<OwnershipComponent>(building)!.PlayerId = 2;
        cm.NotifyOwnerChanged(building, 1, 2);

        Assert.Equal(default, bai.UnitAITarget);
        Assert.Empty(bai.FocusTargets);
    }

    [Fact]
    public void DiplomacyChange_ClearsUnitAITargetOnly()
    {
        var (cm, rm) = NewWorld();
        var building = MakeBuilding(cm, rm);
        var target = MakeEnemyUnit(cm, rm, 110f, 100f);
        Scan(cm, rm, building);
        var bai = Bai(cm, building);
        bai.SetUnitAITarget(target);
        bai.AddFocusTarget(target, queued: false);

        // 与本建筑属主无关的外交变化(2⇄3)→ 不清。
        cm.Events.RaiseDiplomacyChanged(new Events.DiplomacyChangedEvent { Player = 2, OtherPlayer = 3 });
        Assert.Equal(target, bai.UnitAITarget);
        Assert.Single(bai.FocusTargets);

        // 属主(1)对 2 翻面 → 只清 unitAITarget,focusTargets 保留(WS3 裁定)。
        var p1 = cm.Players.GetPlayerEntityId(1)!.Value;
        var p2 = cm.Players.GetPlayerEntityId(2)!.Value;
        cm.QueryInterface<DiplomacyComponent>(p1)!
            .SetStanceToward(1, cm.QueryInterface<DiplomacyComponent>(p2)!, 2, DiplomacyComponent.Ally);
        Assert.Equal(default, bai.UnitAITarget);
        Assert.Single(bai.FocusTargets);
    }

    [Fact]
    public void Volley_UnitAITargetInRange_PromotesToSoleFocusTarget()
    {
        var (cm, rm) = NewWorld();
        var building = MakeBuilding(cm, rm);
        var a = MakeEnemyUnit(cm, rm, 105f, 100f);
        var b = MakeEnemyUnit(cm, rm, 110f, 100f);
        var bai = Bai(cm, building);
        bai.SetUnitAITarget(b);

        rm.UpdateVisibilityData();
        bai.Tick(1.0f, cm);   // 扫描(两敌入表)+ 齐射:升格分支触发

        // 原版 BuildingAI.js:343-344:unitAITarget 已在射程表内 → 升格为唯一 focusTarget。
        Assert.Equal(new[] { b }, bai.FocusTargets);
    }

    [Fact]
    public void Volley_PerArrowLosCheck_SkipsHiddenFocusTarget()
    {
        var (cm, rm) = NewWorld();
        var building = MakeBuilding(cm, rm);
        var target = MakeEnemyUnit(cm, rm, 110f, 100f);
        int fired = 0;
        cm.Events.AttackLaunched += e => { if (e.Target == target) fired++; };
        var bai = Bai(cm, building);

        rm.UpdateVisibilityData();
        bai.Tick(1.0f, cm);                       // 扫描+首轮齐射(可见 → 放箭)
        Assert.Equal(1, fired);
        bai.AddFocusTarget(target, queued: false); // 目标在射程表内 → 接受

        // 扫描间隙目标转入迷雾(1s 刷新窗口内逐箭复核兜底)。
        rm.SetLosRevealAll(1, false);
        rm.UpdateVisibilityData();
        Assert.Equal(LosVisibility.Hidden, rm.GetLosVisibility(target, 1));

        bai.Tick(0.5f, cm);                       // 冷却中,不放箭
        bai.Tick(0.6f, cm);                       // 冷却尽+重扫描(目标 Hidden 出表,
        Assert.Equal(1, fired);                   // 但 focusTarget 仍在:逐箭 LOS 校验拦下)
    }
}

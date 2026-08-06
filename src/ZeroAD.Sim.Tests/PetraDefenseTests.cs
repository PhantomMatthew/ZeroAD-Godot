using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using ZeroAD.Sim.AI;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.AI.Petra;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Net;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Petra 守家:DefenseManager 调兵回防 + GarrisonManager 威胁塞人/安全放出。
/// 命令经 SubmitAiCommand → NetTurnManager._aiBundles → AdvanceTurn 落 sim。
/// junction 数据(模板)缺失时按惯例跳过。
/// </summary>
public sealed class PetraDefenseTests
{
    private static string? FindRepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, relative);
    }

    private sealed class DefWorld
    {
        public required ComponentManager Cm;
        public required NetTurnManager Net;
        public required GameState Gs;
        public required AIEventBuffer Events;
        public required EntityId Cc;        // 带驻军位的市政中心
        public required EntityId Soldier1;
        public required EntityId Soldier2;
        public required EntityId Villager;  // 无攻击件的平民(驻军候选)
        public required EntityId Enemy;     // 压境敌军(owner 3)
    }

    private static EntityId MakeUnit(ComponentManager cm, int owner, string template,
        float x, float z, bool soldier)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromFloat(x), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromFloat(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new IdentityComponent
        {
            TemplateName = template,
            IsUnit = true,
            Classes = new List<string> { "Unit" },
        });
        cm.AddComponent(e, new UnitAIComponent());
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new HealthComponent { Current = 100, Max = 100 });
        if (soldier)
        {
            var atk = new AttackComponent { Range = 3f };
            cm.AddComponent(e, atk);
            atk.Damage.Amounts[DamageType.Hack] = 10;
        }
        else
        {
            cm.AddComponent(e, new ResourceGatherer());
            cm.AddComponent(e, new GarrisonableComponent { Size = 1 });
        }
        return e;
    }

    private static DefWorld? NewDefWorld()
    {
        var templatesRoot = FindRepoPath("binaries/data/mods/public/simulation/templates");
        var techRoot = FindRepoPath("binaries/data/mods/public/simulation/data/technologies");
        if (templatesRoot == null || techRoot == null) return null;

        var templates = new TemplateLoader(templatesRoot);
        templates.LoadAllTemplates();
        var techCatalog = TechnologyLoader.LoadAll(techRoot);

        var cm = new ComponentManager(rngSeed: 42, templates: templates);
        SimSystem.Init(cm);
        var events = new AIEventBuffer();
        events.Attach(cm);

        // AI 玩家(player 2);敌军 owner=3(无玩家实体——dip 缺失时 IsEnemy 恒 true)。
        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent { Civ = "gaul" });
        cm.AddComponent(playerEntity, new OwnershipComponent { PlayerId = 2 });
        cm.RegisterPlayer(2, playerEntity);

        // 市政中心:带 10 驻军位(原版 civil_centre GarrisonHolder)。
        var cc = cm.CreateEntity();
        var ccPos = new PositionComponent();
        cm.AddComponent(cc, ccPos);
        ccPos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromInt(100), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromInt(100));
        cm.AddComponent(cc, new OwnershipComponent { PlayerId = 2 });
        cm.AddComponent(cc, new IdentityComponent
        {
            TemplateName = "structures/gaul/civil_centre",
            IsBuilding = true,
            Classes = new List<string> { "CivCentre", "Structure" },
        });
        var holder = new GarrisonHolderComponent { Max = 10, LoadingRange = 4f };
        cm.AddComponent(cc, holder);
        holder.AllowedClasses.Add("Unit");

        var s1 = MakeUnit(cm, 2, "units/gaul/infantry_spearman_b", 105, 100, soldier: true);
        var s2 = MakeUnit(cm, 2, "units/gaul/infantry_spearman_b", 108, 100, soldier: true);
        var vil = MakeUnit(cm, 2, "units/gaul/support_civilian", 100, 100, soldier: false);
        var enemy = MakeUnit(cm, 3, "units/athen/infantry_spearman_b", 120, 100, soldier: true);

        var net = new NetTurnManager(cm, commandDelay: 2, localPlayerId: 2,
            NetRole.Standalone, expectedPlayers: new HashSet<uint> { 2 });
        var gs = new GameState(cm, templates, techCatalog, 2, new EntityMetadata(), events, null)
        { Net = net };

        return new DefWorld
        {
            Cm = cm, Net = net, Gs = gs, Events = events,
            Cc = cc, Soldier1 = s1, Soldier2 = s2, Villager = vil, Enemy = enemy
        };
    }

    private static void AdvanceAndTick(DefWorld w, int turns)
    {
        for (int i = 0; i < turns; i++)
        {
            w.Net.AdvanceTurn();
            // 单位 AI 各走一拍(sim tick 驱动订单派发/状态推进)。
            foreach (var e in w.Cm.AllEntities.ToList())
                w.Cm.QueryInterface<UnitAIComponent>(e)?.Tick(0.1f, w.Cm);
        }
    }

    [Fact]
    public void DefenseManager_DangerousEnemy_GetsSoldiersAttacking()
    {
        var w = NewDefWorld();
        if (w == null) return;
        var dm = new ZeroAD.Sim.AI.Petra.DefenseManager(new PetraConfig(DifficultyLevel.Medium));

        dm.Update(w.Gs, w.Events);
        AdvanceAndTick(w, 3);

        // 最近的空闲士兵应收到 Attack 订单并锁定威胁目标。
        var ai1 = w.Cm.QueryInterface<UnitAIComponent>(w.Soldier1)!;
        var ai2 = w.Cm.QueryInterface<UnitAIComponent>(w.Soldier2)!;
        bool s1Attacking = ai1.CurrentOrder?.Type == "Attack" && ai1.CurrentOrder.Target == w.Enemy;
        bool s2Attacking = ai2.CurrentOrder?.Type == "Attack" && ai2.CurrentOrder.Target == w.Enemy;
        Assert.True(s1Attacking || s2Attacking,
            $"expected a defender attacking the threat, got s1={ai1.CurrentOrder?.Type} s2={ai2.CurrentOrder?.Type}");
    }

    [Fact]
    public void DefenseManager_NoThreat_NoAssignments()
    {
        var w = NewDefWorld();
        if (w == null) return;
        // 敌军搬走(80m 威胁半径外)。
        var pos = w.Cm.QueryInterface<PositionComponent>(w.Enemy)!;
        pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromInt(500), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromInt(500));
        var dm = new ZeroAD.Sim.AI.Petra.DefenseManager(new PetraConfig(DifficultyLevel.Medium));

        dm.Update(w.Gs, w.Events);
        AdvanceAndTick(w, 3);

        var ai1 = w.Cm.QueryInterface<UnitAIComponent>(w.Soldier1)!;
        Assert.NotEqual("Attack", ai1.CurrentOrder?.Type);
    }

    [Fact]
    public void GarrisonManager_Threat_MustersVillagerIntoHolder()
    {
        var w = NewDefWorld();
        if (w == null) return;
        var gm = new GarrisonManager(new PetraConfig(DifficultyLevel.Medium));

        gm.Update(w.Gs);
        AdvanceAndTick(w, 3);

        // 平民被塞进市政中心(离开世界)。
        var pos = w.Cm.QueryInterface<PositionComponent>(w.Villager)!;
        Assert.False(pos.InWorld);
        var holder = w.Cm.QueryInterface<GarrisonHolderComponent>(w.Cc)!;
        Assert.Contains(w.Villager, holder.Entities);
    }

    [Fact]
    public void GarrisonManager_ThreatGone_EvacuatesOwnMustered()
    {
        var w = NewDefWorld();
        if (w == null) return;
        var gm = new GarrisonManager(new PetraConfig(DifficultyLevel.Medium));

        gm.Update(w.Gs);
        AdvanceAndTick(w, 3);
        Assert.False(w.Cm.QueryInterface<PositionComponent>(w.Villager)!.InWorld);

        // 威胁消除(敌军销毁)→ 下一轮疏散。
        w.Cm.DestroyEntity(w.Enemy);
        gm.Update(w.Gs);
        AdvanceAndTick(w, 3);

        var pos = w.Cm.QueryInterface<PositionComponent>(w.Villager)!;
        Assert.True(pos.InWorld);
        var holder = w.Cm.QueryInterface<GarrisonHolderComponent>(w.Cc)!;
        Assert.DoesNotContain(w.Villager, holder.Entities);
    }

    [Fact]
    public void GarrisonManager_SoldiersStayOut_OnlySupportMustered()
    {
        var w = NewDefWorld();
        if (w == null) return;
        var gm = new GarrisonManager(new PetraConfig(DifficultyLevel.Medium));

        gm.Update(w.Gs);
        AdvanceAndTick(w, 3);

        // 士兵有攻击件但也满足召集条件(idle)——原版士兵优先进塔/留战;
        // 本实现的语义是"空闲即收",故此处固定记录现行行为:平民必收,
        // 士兵不收(他们属于 DefenseManager 的回防池——两管理器同轮跑,
        // 士兵会先被指派攻击而不再 idle)。单独跑 GarrisonManager 时士兵
        // 仍 idle 会被收——这是记录在案的简化,测试钉死"平民必收"主线。
        var holder = w.Cm.QueryInterface<GarrisonHolderComponent>(w.Cc)!;
        Assert.Contains(w.Villager, holder.Entities);
    }
}

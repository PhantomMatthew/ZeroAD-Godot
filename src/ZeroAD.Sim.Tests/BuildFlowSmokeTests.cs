using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

// 全链路冒烟:真实模板 + SimCommandExecutor + 逐回合 tick(镜像 SimBridge.TickSimulation
// 的内核子集)——复现"游戏内下建造/移动指令"的完整内核路径,拦表现层测试覆盖不到的回归
// (如 UnitOrder record 值相等导致的相邻同值订单闷死:订单残留 IDLE → Timer 抛异常 →
// 模拟每帧报错,玩家视角即"游戏莫名停止、无法建造")。
public sealed class BuildFlowSmokeTests
{
    private const string TemplatesRel = "binaries/data/mods/public/simulation/templates";

    /// <summary>从测试程序集向上找到数据树(binaries 是指向上游 0 A.D. 的 junction,
    /// 相对路径在 bin/ 下解析不到——曾因此整组真实模板测试静默跳过)。</summary>
    private static string? FindRepoPath(string relative)
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : System.IO.Path.Combine(dir.FullName, relative);
    }

    private static ComponentManager? SetupWorld()
    {
        string? root = FindRepoPath(TemplatesRel);
        if (root == null) return null;   // 数据树未拉取则跳过
        var cm = new ComponentManager(rngSeed: 1,
            templates: new Content.TemplateLoader(root));
        SimSystem.Init(cm);
        var p1 = cm.CreateEntity();
        var pc = new PlayerComponent();
        cm.AddComponent(p1, pc);
        cm.Players.AddPlayer(1, p1);
        // PlayerComponent.OnInit 会重置资源/人口,AddComponent 之后再发放。
        pc.AddResource(ResourceType.Wood, 5000);
        pc.AddResource(ResourceType.Food, 5000);
        pc.AddResource(ResourceType.Stone, 5000);
        pc.AddResource(ResourceType.Metal, 5000);
        SimSystem.SetRangeManager(new RangeManager(cm, Fixed.FromInt(512), Fixed.FromInt(512)));
        return cm;
    }

    /// <summary>镜像 SimBridge.TickSimulation 的内核子集:motion → UnitAI → builder →
    /// attack → 延迟伤害结算。(采集循环/地基换实体在表现层,内核侧不测。)</summary>
    private static void TickWorld(ComponentManager cm, int turns)
    {
        foreach (var _ in Enumerable.Range(0, turns))
        {
            foreach (var e in cm.AllEntities.ToList())
            {
                cm.QueryInterface<UnitMotion>(e)?.Tick(0.1f);
                cm.QueryInterface<UnitAIComponent>(e)?.Tick(0.1f, cm);
                cm.QueryInterface<BuilderComponent>(e)?.Tick(cm);
                cm.QueryInterface<AttackComponent>(e)?.Tick(0.1f, cm);
            }
            cm.DelayedDamage.TickPending(cm);
            cm.DelayedDamage.AdvanceTurn();
        }
    }

    [Fact]
    public void BuildHouse_EndToEnd_FoundationCompletes()
    {
        var cm = SetupWorld();
        if (cm == null) return;
        var villager = cm!.SpawnEntity("units/spart/support_civilian", 10, 10, ownerPlayerId: 1);

        var exec = new Net.SimCommandExecutor(cm);
        var fx = Fixed.FromFloat(30f);
        var fz = Fixed.FromFloat(10f);
        exec.Apply(new Net.NetCommand(1, Net.NetCommandType.Build, villager.Value,
            fp1: fx.InternalValue, fp2: fz.InternalValue,
            templateName: "structures/spart/house"));

        var fdn = cm.AllEntities
            .Select(e => cm.QueryInterface<FoundationComponent>(e))
            .FirstOrDefault(f => f != null);
        Assert.NotNull(fdn);
        Assert.Equal("structures/spart/house", fdn!.ResultTemplate);

        TickWorld(cm, 1500);   // 走 20m + 建造,150s 足够
        Assert.True(fdn.IsBuilt);
        // 建成收工:工人出表、Builder 目标清空。
        Assert.Equal(0, fdn.NumBuilders);
        Assert.Null(cm.QueryInterface<BuilderComponent>(villager)!.Target);
    }

    [Fact]
    public void AdjacentIdenticalOrders_RejectedPair_DoesNotGetStuck()
    {
        var cm = SetupWorld();
        if (cm == null) return;
        var cc = cm!.SpawnEntity("structures/spart/civil_centre", 0, 0, ownerPlayerId: 1);
        var villager = cm.SpawnEntity("units/spart/support_civilian", 10, 0, ownerPlayerId: 1);
        var ai = cm.QueryInterface<UnitAIComponent>(villager)!;

        // 两条内容完全相同的订单(Queued/Force 也一致 → record 值相等):首张派发时
        // handler 因无携带立即 FinishOrder,次张顶上——值比较会误判"队首未变"把次单
        // 闷死在队列里(CurrentOrder 残留,单位永不执行);必须引用比较。
        ai.ReturnResource(cc, queued: true);
        ai.ReturnResource(cc, queued: true);

        TickWorld(cm, 10);

        Assert.True(ai.IsIdle);
        Assert.Null(ai.CurrentOrder);
    }

    [Fact]
    public void AdjacentIdenticalWalkOrders_BothComplete()
    {
        var cm = SetupWorld();
        if (cm == null) return;
        var villager = cm!.SpawnEntity("units/spart/support_civilian", 0, 0, ownerPlayerId: 1);
        var ai = cm.QueryInterface<UnitAIComponent>(villager)!;

        // 连点同一位置两次(均 queued → 值完全相等):两单都应依次走完,队列清空回 IDLE。
        var pos = new FixedVector2D(Fixed.FromFloat(8), Fixed.Zero);
        ai.Walk(pos, queued: true);
        ai.Walk(pos, queued: true);

        TickWorld(cm, 300);

        Assert.True(ai.IsIdle);
        Assert.Null(ai.CurrentOrder);
        var finalPos = cm.QueryInterface<PositionComponent>(villager)!.Position;
        Assert.Equal(8f, finalPos.X.ToFloat(), 0);
    }
}

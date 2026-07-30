using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 存档往返 → ComputeStateHash 一致性(app 报告:流转完整但 hash 不匹配)。
/// 厨房水槽世界:LOS 系统实体/双玩家+外交/单位(FSM+指令)/建筑(队列+领土+衰减+占领)/
/// 资源树/光环。失配时输出 TextDump 首个差异点上下文,直接定位不对称的组件字段。
/// </summary>
public sealed class SaveLoadRoundTripTests
{
    private static string? FindRepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, relative);
    }

    private static string Dump(ComponentManager cm)
    {
        var text = new TextDumpSerializer();
        cm.SerializeFullState(text);
        return text.ToString();
    }

    private static string FirstDiff(string a, string b)
    {
        var la = a.Split('\n');
        var lb = b.Split('\n');
        var sb = new StringBuilder();
        sb.AppendLine($"dump lines: A={la.Length} B={lb.Length}");
        int n = Math.Min(la.Length, lb.Length);
        for (int i = 0; i < n; i++)
        {
            if (la[i] == lb[i]) continue;
            sb.AppendLine($"first diff at line {i}:");
            for (int j = Math.Max(0, i - 4); j < Math.Min(n, i + 6); j++)
                sb.AppendLine($"  A[{j}]: {la[j]}\n  B[{j}]: {lb[j]}");
            return sb.ToString();
        }
        sb.AppendLine($"common prefix identical; length differs ({la.Length} vs {lb.Length})");
        for (int j = Math.Max(0, n - 3); j < Math.Max(la.Length, lb.Length) && j < n + 5; j++)
            sb.AppendLine($"  A: {(j < la.Length ? la[j] : "<eof>")}\n  B: {(j < lb.Length ? lb[j] : "<eof>")}");
        return sb.ToString();
    }

    private static ComponentManager BuildWorld(ZeroAD.Sim.Content.TemplateLoader? templates, out RangeManager range)
    {
        var cm = new ComponentManager(42, templates: templates);
        // 内核 ctor 不自动注册组件类型(app 由 SimBridge:106 做);存档反序列化按名查注册表。
        cm.Registry.AutoRegister(typeof(PositionComponent).Assembly);
        SimSystem.Init(cm);
        range = new RangeManager(cm, Fixed.FromInt(64), Fixed.FromInt(64));
        range.SetBounds(Fixed.FromInt(64));

        // 系统实体:LOS 网格持有者(app 同款)。
        var sys = cm.CreateEntity();
        var los = new LosManagerComponent();
        cm.AddComponent(sys, los);
        los.Attach(range);

        // 双玩家 + 外交(同队 → 互盟)。
        foreach (var pid in new[] { 1, 2 })
        {
            var pe = cm.CreateEntity();
            var pc = new PlayerComponent();
            cm.AddComponent(pe, pc);
            pc.Wood = 500; pc.Food = 400; pc.Stone = 300; pc.Metal = 200;
            cm.AddComponent(pe, new OwnershipComponent { PlayerId = pid });
            cm.AddComponent(pe, new DiplomacyComponent());
            cm.AddComponent(pe, new TechnologyManager());
            cm.Players.AddPlayer(pid, pe);
        }
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 0 });

        // 单位:FSM + 一条挂起指令(让 UnitAI 序列化非平凡)。
        var unit = cm.CreateEntity();
        cm.AddComponent(unit, new PositionComponent());
        cm.QueryInterface<PositionComponent>(unit)!.Position =
            new FixedVector3D(Fixed.FromInt(10), Fixed.Zero, Fixed.FromInt(10));
        cm.AddComponent(unit, new UnitMotion());
        var ai = new UnitAIComponent();
        cm.AddComponent(unit, ai);
        cm.AddComponent(unit, new IdentityComponent());
        cm.AddComponent(unit, new OwnershipComponent { PlayerId = 1 });
        cm.AddComponent(unit, new HealthComponent());
        // 四种伤害类型取互异非零值:钉死 DamageBlock 读写顺序(crush↔capture 对调事故)。
        var atk = new AttackComponent();
        atk.Damage.Amounts[DamageType.Hack] = 11;
        atk.Damage.Amounts[DamageType.Pierce] = 22;
        atk.Damage.Amounts[DamageType.Crush] = 33;
        atk.Damage.Capture = 44;
        cm.AddComponent(unit, atk);
        cm.AddComponent(unit, new ResourceGatherer());

        // 资源树(gaia)。
        var tree = cm.CreateEntity();
        cm.AddComponent(tree, new PositionComponent());
        cm.QueryInterface<PositionComponent>(tree)!.Position =
            new FixedVector3D(Fixed.FromInt(14), Fixed.Zero, Fixed.FromInt(10));
        cm.AddComponent(tree, new ResourceSupply());
        cm.AddComponent(tree, new OwnershipComponent { PlayerId = 0 });

        // 建筑:生产队列 + 领土三件套 + 占领 + 地基(另一个实体)。
        var cc = cm.CreateEntity();
        cm.AddComponent(cc, new PositionComponent());
        cm.QueryInterface<PositionComponent>(cc)!.Position =
            new FixedVector3D(Fixed.FromInt(32), Fixed.Zero, Fixed.FromInt(32));
        cm.AddComponent(cc, new IdentityComponent());
        cm.AddComponent(cc, new OwnershipComponent { PlayerId = 1 });
        var queue = new ProductionQueue();
        cm.AddComponent(cc, queue);
        cm.AddComponent(cc, new TerritoryInfluenceComponent
        {
            Radius = Fixed.FromInt(24),
            Weight = 10000,
            Root = true,
        });
        cm.AddComponent(cc, new TerritoryDecayComponent
        {
            DecayRate = Fixed.FromInt(20),
            Territory = "neutral enemy",
        });
        var cap = new CapturableComponent
        {
            MaxCapturePoints = Fixed.FromInt(500),
            RegenRate = Fixed.FromInt(5),
        };
        cm.AddComponent(cc, cap);
        cap.InitForOwner(1);
        cm.AddComponent(cc, new BuildRestrictionsComponent { Territory = "own neutral" });
        cm.AddComponent(cc, new HealthComponent());

        var foundation = cm.CreateEntity();
        cm.AddComponent(foundation, new PositionComponent());
        cm.QueryInterface<PositionComponent>(foundation)!.Position =
            new FixedVector3D(Fixed.FromInt(40), Fixed.Zero, Fixed.FromInt(40));
        cm.AddComponent(foundation, new FoundationComponent());
        cm.AddComponent(foundation, new IdentityComponent());
        cm.AddComponent(foundation, new OwnershipComponent { PlayerId = 2 });

        // 两条互异指令(target + x≠z 位置 + queued 标志):钉死 UnitAI 指令读写顺序
        // (target 被对象初始化器拖到最后、px/pz 对调并流脱同步事故)。
        ai.Gather(tree);
        ai.Walk(new FixedVector2D(Fixed.FromInt(3), Fixed.FromInt(51)), queued: true);

        return cm;
    }

    [Fact]
    public void SaveLoad_RoundTrip_StateHashMatches()
    {
        var templatesPath = FindRepoPath("binaries/data/mods/public/simulation/templates");
        var templates = templatesPath != null ? new Content.TemplateLoader(templatesPath) : null;

        var cmA = BuildWorld(templates, out _);
        byte[] hashA = cmA.ComputeStateHash();
        string dumpA = Dump(cmA);

        var ms = new MemoryStream();
        cmA.SerializeSaveGame(new BinarySerializer(new BinaryWriter(ms)));
        ms.Position = 0;

        var cmB = new ComponentManager(42, templates: templates);
        cmB.Registry.AutoRegister(typeof(PositionComponent).Assembly);
        SimSystem.Init(cmB);
        var rangeB = new RangeManager(cmB, Fixed.FromInt(64), Fixed.FromInt(64));
        rangeB.SetBounds(Fixed.FromInt(64));
        cmB.DeserializeSaveGame(new BinaryDeserializer(new BinaryReader(ms)), comp =>
        {
            if (comp is LosManagerComponent l) l.Attach(rangeB);
        });

        byte[] hashB = cmB.ComputeStateHash();
        if (!hashA.AsSpan().SequenceEqual(hashB))
            Assert.Fail(FirstDiff(dumpA, Dump(cmB)));
    }

    /// <summary>真存档(app quicksave.zsave)第二代往返:读入 → hash → 再存 → 再读 → hash。
    /// 同代码代际内对比,消除存档写入时点与当前组件集的版本偏移;流完整消费断言兜底
    /// 格式漂移。无真存档的环境直接跳过(同 LFS 惯例)。</summary>
    [Fact]
    public void RealQuicksave_RoundTrip_StateHashMatches()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Godot", "app_userdata", "0 A.D. Godot Rewrite", "saves", "quicksave.zsave");
        if (!File.Exists(path)) return;

        ComponentManager Load(byte[] payload)
        {
            var cm = new ComponentManager(42);
            cm.Registry.AutoRegister(typeof(PositionComponent).Assembly);
            SimSystem.Init(cm);
            var range = new RangeManager(cm, Fixed.FromInt(256), Fixed.FromInt(256));
            cm.DeserializeSaveGame(new BinaryDeserializer(new BinaryReader(new MemoryStream(payload))), comp =>
            {
                if (comp is LosManagerComponent l) l.Attach(range);
                if (comp is AIComponent ai) ai.Configure(cm, null!);
            });
            return cm;
        }

        var raw = File.ReadAllBytes(path);
        using (var head = new BinaryReader(new MemoryStream(raw)))
        {
            Assert.Equal("0ADSAVE", Encoding.ASCII.GetString(head.ReadBytes(7)));
            uint version = head.ReadUInt32();
            // v2(2026-07-29)起 HealthComponent 增 Unhealable + HealComponent 增计时器字段;
            // v1 旧档位置流错位不可读,跳过(与 app 端 SaveGameManager 版本拒收一致)。
            if (version != 2u) return;
            head.ReadUInt32();                     // turn(无关 hash)
        }
        byte[] payload = raw.Skip(7 + 4 + 4).ToArray();

        var cmA = Load(payload);
        byte[] hashA = cmA.ComputeStateHash();
        string dumpA = Dump(cmA);

        var resave = new MemoryStream();
        cmA.SerializeSaveGame(new BinarySerializer(new BinaryWriter(resave)));
        var cmB = Load(resave.ToArray());
        byte[] hashB = cmB.ComputeStateHash();

        if (!hashA.AsSpan().SequenceEqual(hashB))
            Assert.Fail(FirstDiff(dumpA, Dump(cmB)));
    }
}

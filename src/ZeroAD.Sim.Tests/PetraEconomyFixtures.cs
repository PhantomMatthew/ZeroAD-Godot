using System;
using System.IO;
using ZeroAD.Sim.AI;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.AI.Petra;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Net;

namespace ZeroAD.Sim.Tests;

/// <summary>Petra 测试共享夹具(原 PetraEconomyTests 私有 NewAiWorld 提取;
/// PetraSerializationTests 等复用):真模板目录 + gaul AI 玩家 + CC + worker +
/// NetTurnManager/GameState/Headquarters。</summary>
internal static class PetraEconomyFixtures
{
    public static string? FindRepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, relative);
    }

    public sealed class AiWorld
    {
        public required ComponentManager Cm;
        public required NetTurnManager Net;
        public required GameState Gs;
        public required Headquarters Hq;
        public required AIEventBuffer Events;
        public required EntityId Cc;
        public required EntityId Worker;
    }

    public static AiWorld? NewAiWorld()
    {

        var templatesRoot = FindRepoPath("binaries/data/mods/public/simulation/templates");
        var techRoot = FindRepoPath("binaries/data/mods/public/simulation/data/technologies");
        if (templatesRoot == null || techRoot == null) return null;

        var templates = new TemplateLoader(templatesRoot);
        templates.LoadAllTemplates();
        var techCatalog = TechnologyLoader.LoadAll(techRoot);

        var cm = new ComponentManager(rngSeed: 42, templates: templates);
        SimSystem.Init(cm);
        // RangeManager:entity 前置(phase 科技需 N 个 Village 建筑)从范围索引计数。
        SimSystem.SetRangeManager(new RangeManager(cm,
            ZeroAD.Sim.Maths.Fixed.FromInt(256), ZeroAD.Sim.Maths.Fixed.FromInt(256)));
        var events = new AIEventBuffer();
        events.Attach(cm);   // 实体创建即录事件(AIComponent 同款;turnMod 轮转靠它)

        // AI 玩家实体(player 2,gaul)
        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent { Civ = "gaul" });
        cm.AddComponent(playerEntity, new OwnershipComponent { PlayerId = 2 });
        cm.RegisterPlayer(2, playerEntity);
        // 科技管理器(研究命令路径需要;与 SimBridge InitWorld 同款配置)。
        var techMgr2 = new TechnologyManager();
        techMgr2.Configure(techCatalog, "gaul");
        cm.AddComponent(playerEntity, techMgr2);
        // 本地玩家实体(player 1,旁观)
        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent { Civ = "athen" });
        cm.AddComponent(p1, new OwnershipComponent { PlayerId = 1 });
        cm.RegisterPlayer(1, p1);

        // AI 的 CC(可训练 female citizen 的 trainer)
        var cc = cm.CreateEntity();
        cm.AddComponent(cc, new PositionComponent());
        cm.AddComponent(cc, new OwnershipComponent { PlayerId = 2 });
        cm.AddComponent(cc, new IdentityComponent
        {
            TemplateName = "structures/gaul/civil_centre",
            IsBuilding = true,
            Classes = new System.Collections.Generic.List<string> { "CivCentre", "Structure" },
        });
        cm.AddComponent(cc, new ProductionQueue
        {
            TrainableTokens = "units/{civ}/support_civilian units/{civ}/infantry_spearman_b",
            NativeCiv = "gaul",
        });

        // AI 的村民(可建造的 builder;role 不设——CountOwnEntitiesByRole("worker")=0
        // → TrainMoreWorkers 必触发,正好验证训练链)
        var worker = cm.CreateEntity();
        cm.AddComponent(worker, new PositionComponent());
        cm.AddComponent(worker, new OwnershipComponent { PlayerId = 2 });
        cm.AddComponent(worker, new IdentityComponent
        {
            TemplateName = "units/gaul/support_civilian",
            IsUnit = true,
            Classes = new System.Collections.Generic.List<string> { "Citizen", "Unit" },
        });
        cm.AddComponent(worker, new ResourceGatherer());

        var net = new NetTurnManager(cm, commandDelay: 2, localPlayerId: 2,
            NetRole.Standalone, expectedPlayers: new System.Collections.Generic.HashSet<uint> { 2 });
        var metadata = new EntityMetadata();
        var gs = new GameState(cm, templates, techCatalog, 2, metadata, events, null)
        { Net = net };
        var hq = new Headquarters(new PetraConfig(DifficultyLevel.Medium));
        // 首回合初始化(AIComponent.Tick 同款):注册首基地 → HasActiveBase=true → 经济循环可运行
        StartingStrategy.GameAnalysis(hq, gs);
        StartingStrategy.BuildFirstBase(hq, gs);
        StartingStrategy.ConfigFirstBase(hq, gs);

        return new AiWorld { Cm = cm, Net = net, Gs = gs, Hq = hq, Events = events, Cc = cc, Worker = worker };
    }

}

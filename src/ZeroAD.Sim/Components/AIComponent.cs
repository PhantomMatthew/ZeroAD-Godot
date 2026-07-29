using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI;
using ZeroAD.Sim.Net;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>
/// AI 大脑内核组件(Phase 2 字面"迁入内核")。对齐原 Godot 层 PetraAI + PetraManagers,
/// 但作为可序列化 <see cref="ComponentBase"/> 挂在 AI 玩家实体上 → brain 状态进
/// <see cref="ComponentManager.SerializeFullState"/>/OOS hash/存档 → 撤 MP 闸门。
///
/// 确定性契约(各端同跑同生成,故 AI 无需网络槽):
/// - 思考节律:<see cref="NetTurnManager.CurrentTurn"/> 驱动(非墙钟),每 <see cref="ThinkIntervalTurns"/> 回合思考。
/// - 随机:<see cref="ComponentManager.RNG"/>(内核 Rand48,序列化进 hash + 存档)。
/// - 下令:<see cref="NetTurnManager.SubmitAiCommand"/> —— AI 专用本地通道,永不进网络 outbox。
///   各端 AIComponent 状态相同 → 生成相同命令 → 各端 _aiBundles 一致 → 无 OOS。
///
/// 生命周期:Configure 注入 cm/net(AuraComponent.Configure 同款,AddComponent 前调;
/// save/load 由 prepareComponent 重注入)。playerId 单一真源——从本实体 OwnershipComponent 派生,
/// 免 Configure 时 Entity 未 SetEntity。派生态(owned 列表)不序列化,靠 tick 重建。
/// </summary>
[Component("AiBrain", "AiBrain")]
public sealed class AIComponent : ComponentBase
{
    private ComponentManager? _cm;
    private NetTurnManager? _net;

    private EconomyManager? _economy;
    private BuildManager? _build;
    private ResearchManager? _research;
    private DefenseManager? _defense;
    private AttackManager? _attack;

    // 回合驱动思考节律(替代旧帧计时)。5 回合 ≈ 0.5s @ 10Hz,对齐原 ThinkInterval。
    // CurrentTurn 门控 → 同 seed 同回合同思考 tick(跨对端/读档)。
    private const int ThinkIntervalTurns = 5;
    private uint _lastThinkTurn = uint.MaxValue;

    // 每 think 从活实体图重建(owner == playerId)。派生态,不序列化。
    private readonly List<EntityId> _ownedUnits = new();
    private readonly List<EntityId> _ownedBuildings = new();

    /// <summary>装配期注入内核引用。须在 <see cref="ComponentManager.AddComponent{T}"/> 前调
    /// (AddComponent 触发 OnInit);save/load 路径由 prepareComponent 重注入(见 SaveGameManager.Load)。</summary>
    public void Configure(ComponentManager cm, NetTurnManager net)
    {
        _cm = cm;
        _net = net;
        _economy = new EconomyManager(cm, net);
        _build = new BuildManager(cm, net);
        _research = new ResearchManager(cm, net);
        _defense = new DefenseManager(cm, net);
        _attack = new AttackManager(cm, net);
    }

    /// <summary>每 sim 回合入口。SimBridge.TickAI 在 TickSimulation 后、AdvanceTurn 前调,
    /// 故 SubmitAiCommand 落 currentTurn+commandDelay 批次,与人手同路径同延迟。
    /// 每 <see cref="ThinkIntervalTurns"/> 回合思考一次。</summary>
    public void Tick()
    {
        if (_cm == null || _net == null || _economy == null || _build == null
            || _research == null || _defense == null || _attack == null) return;

        uint turn = _net.CurrentTurn;
        if (_lastThinkTurn != uint.MaxValue && turn - _lastThinkTurn < ThinkIntervalTurns) return;
        _lastThinkTurn = turn;

        // playerId 单一真源:本实体(玩家实体)的 OwnershipComponent。免 Configure 时漂移。
        var owner = _cm.QueryInterface<OwnershipComponent>(Entity);
        if (owner == null || owner.PlayerId <= 0) return;
        uint playerId = (uint)owner.PlayerId;

        RebuildOwned(playerId);

        var player = _cm.GetPlayerEntity(owner.PlayerId);
        if (player == null) return;

        var snapshot = new AISnapshot
        {
            Player = player,
            Villagers = _ownedUnits.Where(u => _cm.QueryInterface<ResourceGatherer>(u) != null).ToList(),
            Soldiers = _ownedUnits.Where(u => _cm.QueryInterface<AttackComponent>(u) != null).ToList(),
            Buildings = _ownedBuildings.ToList(),
            EnemyUnits = FindEnemyUnits(playerId),
            EnemyBuildings = FindEnemyBuildings(playerId),
        };

        _economy.Update(snapshot, playerId);
        _build.Update(snapshot, playerId);
        _research.Update(snapshot, playerId);
        _defense.Update(snapshot, playerId);
        _attack.Update(snapshot, playerId);
    }

    /// <summary>重建 owned 列表。死实体已不在 AllEntities,故兼作清理——无需单独死实体剪枝。
    /// 确定性:遍历 AllEntities 存储序。</summary>
    private void RebuildOwned(uint playerId)
    {
        _ownedUnits.Clear();
        _ownedBuildings.Clear();
        foreach (var entity in _cm!.AllEntities)
        {
            var o = _cm.QueryInterface<OwnershipComponent>(entity);
            if (o == null || o.PlayerId != playerId) continue;
            var identity = _cm.QueryInterface<IdentityComponent>(entity);
            if (identity == null) continue;
            if (identity.IsBuilding) _ownedBuildings.Add(entity);
            else if (identity.IsUnit) _ownedUnits.Add(entity);
        }
    }

    private List<EntityId> FindEnemyUnits(uint playerId)
    {
        var result = new List<EntityId>();
        foreach (var entity in _cm!.AllEntities)
        {
            var o = _cm.QueryInterface<OwnershipComponent>(entity);
            // 外交敌对过滤(对齐原版):盟友/中立不入列;gaia(0)=敌;无外交数据默认=敌。
            if (o == null || !_cm.Players.IsEnemy((int)playerId, o.PlayerId)) continue;
            var identity = _cm.QueryInterface<IdentityComponent>(entity);
            var attack = _cm.QueryInterface<AttackComponent>(entity);
            if (identity != null && identity.IsUnit && attack != null)
                result.Add(entity);
        }
        return result;
    }

    private List<EntityId> FindEnemyBuildings(uint playerId)
    {
        var result = new List<EntityId>();
        foreach (var entity in _cm!.AllEntities)
        {
            var o = _cm.QueryInterface<OwnershipComponent>(entity);
            // 外交敌对过滤(对齐原版):盟友/中立不入列;gaia(0)=敌;无外交数据默认=敌。
            if (o == null || !_cm.Players.IsEnemy((int)playerId, o.PlayerId)) continue;
            var identity = _cm.QueryInterface<IdentityComponent>(entity);
            if (identity != null && identity.IsBuilding)
                result.Add(entity);
        }
        return result;
    }

    protected override void OnDeinit()
    {
        // 派生态清空(AI 不挂 modifier,无需像 AuraComponent 那样清残留)。
        _ownedUnits.Clear();
        _ownedBuildings.Clear();
    }

    public override void Serialize(ISerializer s)
    {
        // 全标量,无集合 → 无需排序。manager 计数器经引用读(Configure 构造后非 null)。
        s.NumberU32("lastThinkTurn", _lastThinkTurn);
        s.NumberI32("allocThinkCount", _economy?._allocThinkCount ?? 0);
        s.NumberI32("buildThinkCount", _build?._buildThinkCount ?? 0);
        s.NumberI32("researchThinkCount", _research?._researchThinkCount ?? 0);
        s.NumberI32("attackThinkCount", _attack?._attackThinkCount ?? 0);
        s.NumberI32("targetVillagers", _economy?._targetVillagers ?? 12);
    }

    public override void Deserialize(IDeserializer d)
    {
        // 严格按 Serialize 序读取(BinaryDeserializer 按序读,name 仅文档)。
        _lastThinkTurn = d.NumberU32("lastThinkTurn");
        int allocThinkCount = d.NumberI32("allocThinkCount");
        int buildThinkCount = d.NumberI32("buildThinkCount");
        int researchThinkCount = d.NumberI32("researchThinkCount");
        int attackThinkCount = d.NumberI32("attackThinkCount");
        int targetVillagers = d.NumberI32("targetVillagers");

        // manager 由 Configure(prepareComponent 在 Deserialize 前调)已构造 → 还原计数器。
        if (_economy != null) { _economy._allocThinkCount = allocThinkCount; _economy._targetVillagers = targetVillagers; }
        if (_build != null) _build._buildThinkCount = buildThinkCount;
        if (_research != null) _research._researchThinkCount = researchThinkCount;
        if (_attack != null) _attack._attackThinkCount = attackThinkCount;
    }
}

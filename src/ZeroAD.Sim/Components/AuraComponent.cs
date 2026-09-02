using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>
/// 光环组件(对齐原版 simulation/components/Auras.js,551 行)。
/// MVP 覆盖 range / global / player 三型(数据集 137/151 ≈ 91%)。
///
/// 三型应用语义:
/// - <b>range</b>:每 tick <c>RangeManager.ExecuteQuery</c> 主动查 + 与上 tick target 集合 diff。
///   内核无 active-query/OnRangeUpdate 事件(原版靠事件驱动),故改每 tick 全查。
/// - <b>global / player</b>:target = 源 owner 的玩家实体(TechnologyManager 同款)。
///   requiredTechnology 门控:每 tick 重判 <c>IsResearched</c>,翻转时 add/remove。
///
/// modId 规则(D1,对齐 Auras.js:22-27):stackable → <c>aura/&lt;name&gt;&lt;sourceEntity&gt;</c>;
/// 非 stack → <c>aura/&lt;name&gt;</c>。AddModifiers 拒重保证 range 每 tick 对在范围内 target
/// 重复 Add 安全(幂等)。
///
/// affectedPlayers(对齐 Auras.js CalculateAffectedPlayers):支持全 5 token ——
/// <c>Player</c>=自身;<c>Ally</c>=视 owner 为盟友的玩家(单向,含自身);
/// <c>MutualAlly</c>=双向盟友(含自身);<c>ExclusiveMutualAlly</c>=双向盟友(排自身);
/// <c>Enemy</c>=视 owner 为敌的玩家(不含自身;gaia 无玩家实体自然排除)。
/// range 预过滤 target owner ∈ affected 集合;global/player 应用于每个受影响玩家实体。
/// 外交翻转无需事件:每 tick 重算 affected 集合 + diff(与 reqTech 翻转同机制)。
///
/// 生命周期(D4):源销毁时 OnDeinit 清残留 modifier。OnDeinit 无参,故 Configure 注入
/// ComponentManager 引用。派生态(_rangeTargets / _appliedByModId)不序列化,靠 tick 重建。
/// </summary>
[Component("Auras", "Auras")]
public sealed class AuraComponent : ComponentBase
{
    private IReadOnlyList<string> _names = Array.Empty<string>();
    private ComponentManager? _cm;

    /// <summary>range aura name → 上 tick 命中 target 集合(diff 用);player/global 也复用
    /// 记录已 apply target(OnDeinit 清理 + reqTech 翻转)。派生态,不序列化。</summary>
    private readonly Dictionary<string, HashSet<EntityId>> _rangeTargets = new();

    /// <summary>已 apply 的 (modId, target) 清单 —— OnDeinit 遍历清残留(modId 含 source entity,
    /// 故 modId 作 key 足以区分 stackable/非 stack + 多 aura)。</summary>
    private readonly Dictionary<string, HashSet<EntityId>> _appliedByModId = new();

    /// <summary>装配期注入 aura 名(template &lt;Auras&gt; 空格分词)+ ComponentManager 引用
    /// (OnDeinit 清理用)。须在 <see cref="ComponentManager.AddComponent{T}"/> 前调用:
    /// AddComponent 会触发 OnInit,此后字段已就绪。</summary>
    public void Configure(IEnumerable<string> names, ComponentManager cm)
    {
        _names = names.ToList();
        _cm = cm;
    }

    /// <summary>有编队光环(原版 HasFormationAura:任一挂名 type=="formation")。</summary>
    public bool HasFormationAura(AuraCatalog catalog) =>
        _names.Any(n => catalog.Auras.TryGetValue(n, out var def) && def.Type == "formation");

    /// <summary>原版 ApplyFormationAura:编队光环应用到编队成员(affects 类过滤 +
    /// affectedPlayers 属主过滤;modId 含源实体,编队解散/成员离队时成对移除)。
    /// 不走 Tick——只由 Formation 成员变更驱动(原版同款)。</summary>
    public void ApplyFormationAura(ComponentManager cm, AuraCatalog catalog, IReadOnlyList<EntityId> memberIds)
    {
        var owner = cm.QueryInterface<OwnershipComponent>(Entity);
        if (owner == null || owner.PlayerId <= 0) return;
        foreach (var name in _names)
        {
            if (!catalog.Auras.TryGetValue(name, out var def)) continue;
            if (def.Type != "formation" || def.Modifications.Count == 0) continue;
            var affected = ComputeAffectedPlayers(cm, def.AffectedPlayers, owner.PlayerId);
            string modId = ModId(def);
            var targets = new HashSet<EntityId>();
            foreach (var member in memberIds)
            {
                var id = cm.QueryInterface<IdentityComponent>(member);
                if (id == null || !AffectsTarget(def.Affects, id)) continue;
                var o = cm.QueryInterface<OwnershipComponent>(member);
                if (o == null || !affected.Contains(o.PlayerId)) continue;
                targets.Add(member);
            }
            if (targets.Count == 0) continue;
            foreach (var t in targets)
            {
                cm.Modifiers.AddModifiers(modId, def.Modifications, t);
                Track(modId, t, true);
            }
        }
    }

    /// <summary>原版 RemoveFormationAura:成对移除(成员离队/编队解散)。</summary>
    public void RemoveFormationAura(ComponentManager cm, AuraCatalog catalog,
        IReadOnlyList<EntityId> memberIds)
    {
        foreach (var name in _names)
        {
            if (!catalog.Auras.TryGetValue(name, out var def)) continue;
            if (def.Type != "formation") continue;
            string modId = ModId(def);
            foreach (var t in memberIds)
            {
                cm.Modifiers.RemoveAllModifiers(modId, t);
                Track(modId, t, false);
            }
        }
    }

    /// <summary>每 tick 应用/移除光环。由 SimBridge.TickAuras 显式调用(对齐 TickResearch)。</summary>
    public void Tick(ComponentManager cm, RangeManager range, AuraCatalog catalog)
    {
        if (_names.Count == 0) return;

        var owner = cm.QueryInterface<OwnershipComponent>(Entity);
        // 无主或 gaia 不发光环(原版 owner==invalid 跳过)。
        if (owner == null || owner.PlayerId <= 0) return;

        var playerEntity = cm.GetPlayerEntityId(owner.PlayerId);
        if (playerEntity == null) return;

        // TechnologyManager 挂在玩家实体上(reqTech 门控判定)。
        var techMgr = cm.QueryInterface<TechnologyManager>(playerEntity.Value);

        foreach (var name in _names)
        {
            if (!catalog.Auras.TryGetValue(name, out var def)) continue;
            // 空修正值(纯描述 aura)无 effect,跳过避免空 AddModifiers。
            if (def.Modifications.Count == 0) continue;

            var affected = ComputeAffectedPlayers(cm, def.AffectedPlayers, owner.PlayerId);
            if (affected.Count == 0) continue;

            string modId = ModId(def);
            switch (def.Type)
            {
                case "range":
                    TickRange(cm, range, def, modId, affected);
                    break;
                case "global":
                case "player":
                    TickPlayerLevel(cm, def, modId, affected, techMgr);
                    break;
            }
        }
    }

    private void TickRange(ComponentManager cm, RangeManager range, AuraDefinition def,
        string modId, HashSet<int> affected)
    {
        // ExecuteQuery predicate:有 Identity + affects 命中 + target owner ∈ affected 集合。
        var current = new HashSet<EntityId>(range.ExecuteQuery(
            Entity, Maths.Fixed.Zero, Maths.Fixed.FromFloat(def.Radius),
            eid =>
            {
                var id = cm.QueryInterface<IdentityComponent>(eid);
                if (id == null) return false;
                if (!AffectsTarget(def.Affects, id)) return false;
                var o = cm.QueryInterface<OwnershipComponent>(eid);
                return o != null && affected.Contains(o.PlayerId);
            }));

        _rangeTargets.TryGetValue(def.Name, out var prev);
        prev ??= new HashSet<EntityId>();

        // diff:新增 add modifier,离开 remove。AddModifiers 幂等(同 modId+path+target 拒重)。
        foreach (var added in current.Except(prev))
        {
            cm.Modifiers.AddModifiers(modId, def.Modifications, added);
            Track(modId, added, true);
        }
        foreach (var removed in prev.Except(current))
        {
            cm.Modifiers.RemoveAllModifiers(modId, removed);
            Track(modId, removed, false);
        }

        _rangeTargets[def.Name] = current;
    }

    private void TickPlayerLevel(ComponentManager cm, AuraDefinition def, string modId,
        HashSet<int> affected, TechnologyManager? techMgr)
    {
        // reqTech 门控看 SOURCE owner 的科技(对齐原版 IsAuraReqsMet),每 tick 重判。
        bool reqMet = def.RequiredTechnology == null
            || (techMgr != null && techMgr.IsResearched(def.RequiredTechnology));
        var current = new HashSet<EntityId>();
        if (reqMet)
            foreach (var pid in affected)
            {
                var pe = cm.GetPlayerEntityId(pid);
                if (pe != null) current.Add(pe.Value);
            }

        _appliedByModId.TryGetValue(modId, out var prev);
        prev ??= new HashSet<EntityId>();

        // diff 应用/移除(reqTech 翻转 + 外交翻转同机制)。先 materialize:Track 就地改
        // _appliedByModId[modId](与 prev 同集合),不能边枚举边改。
        foreach (var added in current.Except(prev).ToList())
        {
            cm.Modifiers.AddModifiers(modId, def.Modifications, added);
            Track(modId, added, true);
        }
        foreach (var removed in prev.Except(current).ToList())
        {
            cm.Modifiers.RemoveAllModifiers(modId, removed);
            Track(modId, removed, false);
        }
    }

    /// <summary>源销毁:清所有已 apply 的残留 modifier(对齐原版 Clean)。</summary>
    protected override void OnDeinit()
    {
        if (_cm == null) return;
        foreach (var (modId, targets) in _appliedByModId)
            foreach (var t in targets)
                _cm.Modifiers.RemoveAllModifiers(modId, t);
        _appliedByModId.Clear();
        _rangeTargets.Clear();
    }

    public override void Serialize(ISerializer s)
    {
        // names 来自 template(确定性),存一份为状态哈希一致;派生态不存。
        var ordered = _names.OrderBy(n => n, StringComparer.Ordinal).ToList();
        s.NumberI32("count", ordered.Count);
        foreach (var n in ordered) s.StringASCII("aura", n);
    }

    public override void Deserialize(IDeserializer d)
    {
        int count = d.NumberI32("count");
        var names = new List<string>(count);
        for (int i = 0; i < count; i++) names.Add(d.StringASCII("aura"));
        _names = names;
        // _cm 由装配路径 Configure 注入;反序列化路径下 tick 前由 EntityAssembler 重 Configure。
    }

    private void Track(string modId, EntityId target, bool applying)
    {
        if (!_appliedByModId.TryGetValue(modId, out var set))
            _appliedByModId[modId] = set = new HashSet<EntityId>();
        if (applying) set.Add(target); else set.Remove(target);
    }

    private string ModId(AuraDefinition def) =>
        def.Stackable ? $"aura/{def.Name}{Entity.Value}" : $"aura/{def.Name}";

    /// <summary>对齐 Auras.js CalculateAffectedPlayers:Player=自身;Ally=视 owner 为盟友的
    /// 玩家(单向,含自身 —— Player.js IsAlly(self) 恒真);MutualAlly=双向盟友(含自身);
    /// ExclusiveMutualAlly=双向盟友(排自身);Enemy=视 owner 为敌的玩家(不含自身;gaia
    /// 无玩家实体自然排除)。</summary>
    private static HashSet<int> ComputeAffectedPlayers(ComponentManager cm,
        IReadOnlyList<string> tokens, int ownerPlayerId)
    {
        var affected = new HashSet<int>();
        if (tokens.Contains("Player") || tokens.Contains("Ally") || tokens.Contains("MutualAlly"))
            affected.Add(ownerPlayerId);

        bool wantsAlly = tokens.Contains("Ally");
        bool wantsMutual = tokens.Contains("MutualAlly") || tokens.Contains("ExclusiveMutualAlly");
        bool wantsEnemy = tokens.Contains("Enemy");
        if (!wantsAlly && !wantsMutual && !wantsEnemy) return affected;

        DiplomacyComponent? ownerDip = null;
        var ownerEntity = cm.GetPlayerEntityId(ownerPlayerId);
        if (ownerEntity != null)
            ownerDip = cm.QueryInterface<DiplomacyComponent>(ownerEntity.Value);

        foreach (var pid in cm.Players.GetNonGaiaPlayerIds())
        {
            if (pid == ownerPlayerId) continue;
            var pe = cm.GetPlayerEntityId(pid);
            if (pe == null) continue;
            var dip = cm.QueryInterface<DiplomacyComponent>(pe.Value);
            if (dip == null) continue;
            bool alliesOwner = dip.IsAlly(ownerPlayerId);
            if (wantsAlly && alliesOwner) affected.Add(pid);
            if (wantsMutual && alliesOwner && ownerDip != null && ownerDip.IsAlly(pid)) affected.Add(pid);
            if (wantsEnemy && dip.IsEnemy(ownerPlayerId)) affected.Add(pid);
        }
        return affected;
    }

    /// <summary>affects 过滤:空=全中;数组任一 term 命中(term 内空格分词 AND,对齐原版)。</summary>
    private static bool AffectsTarget(IReadOnlyList<string> affects, IdentityComponent id)
    {
        if (affects.Count == 0) return true;
        foreach (var term in affects)
            if (id.MatchesClassList(term)) return true;
        return false;
    }
}

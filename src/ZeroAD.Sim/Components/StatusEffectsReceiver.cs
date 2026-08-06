using System;
using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>一个进行中的状态效果(原版 StatusEffectsReceiver 的 activeStatusEffects 条目)。
/// 数据由施加方(攻击效果/未来科技)构造;Damage/Capture 为每间隔的原始量
/// (结算时过目标抗性,同普通攻击)。</summary>
public sealed class ActiveStatusEffect
{
    public string BaseCode = "";
    /// <summary>持续毫秒;0 = 无时限(仅修饰类,需显式 RemoveStatus)。</summary>
    public float DurationMs;
    /// <summary>执行间隔毫秒;0 = 不周期执行(仅修饰类)。</summary>
    public float IntervalMs;
    /// <summary>每间隔造成的物理伤害(raw,结算时过抗性)。</summary>
    public DamageBlock Damage = new();
    /// <summary>每间隔的捕获量。</summary>
    public Maths.Fixed Capture;
    /// <summary>叠放规则:Ignore(默认)|Extend|Replace|Stack。</summary>
    public string Stackability = "Ignore";
    /// <summary>持续期间的修正值(进入时 AddModifiers,结束时 RemoveAllModifiers)。</summary>
    public List<Modification> Mods = new();
    /// <summary>来源实体(击杀归属;可为空 = 无来源)。</summary>
    public EntityId SourceEntity;
    public int SourceOwner = -1;

    internal float TimeElapsedMs;      // 首次执行起累计(对齐原版 _timeElapsed)
    internal float SinceLastExecMs;    // 距上次执行的累计(回合制近似原版定时器)
    internal bool FirstTime = true;
}

/// <summary>状态效果接收器(原版 StatusEffectsReceiver.js 移植;template_unit 默认空件)。
/// 原版由攻击效果(带 StatusEffects 的攻击)调 ApplyStatus 施加;本上游数据尚无施加源,
/// 本组件作为框架先行:叠放四规则、修饰进出、周期伤害/捕获、时限移除全实现。
/// 回合制近似:原版用 Timer 精确定时;本组件每 sim 回合(0.1s)累加,跨间隔即执行。</summary>
[Component("StatusEffectsReceiver", "StatusEffectsReceiver")]
public sealed class StatusEffectsReceiverComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>原版 DefaultInterval:无 Interval 的效果按 1s 推进 GUI 计时。</summary>
    public const float DefaultIntervalMs = 1000f;

    private readonly Dictionary<string, ActiveStatusEffect> _active = new(StringComparer.Ordinal);

    /// <summary>当前生效的状态(key = 状态码;Stack 规则下为 baseCode_i)。</summary>
    public IReadOnlyDictionary<string, ActiveStatusEffect> ActiveStatuses => _active;

    protected override void OnInit() { }

    /// <summary>施加一个状态效果(原版 AddStatus)。返回实际入库的状态码
    /// (Ignore 命中已有 → null)。</summary>
    public string? AddStatus(ComponentManager cm, string baseCode, ActiveStatusEffect data,
        EntityId attacker, int attackerOwner)
    {
        string statusCode = baseCode;
        if (_active.TryGetValue(statusCode, out var existing))
        {
            switch (data.Stackability)
            {
                case "Ignore":
                    return null;
                case "Extend":
                    existing.DurationMs += data.DurationMs;
                    return statusCode;
                case "Replace":
                    RemoveStatus(cm, statusCode);
                    break;
                case "Stack":
                    int i = 0;
                    while (_active.ContainsKey(baseCode + "_" + i)) i++;
                    statusCode = baseCode + "_" + i;
                    break;
            }
        }

        data.BaseCode = baseCode;
        data.SourceEntity = attacker;
        data.SourceOwner = attackerOwner;
        _active[statusCode] = data;

        if (data.Mods.Count > 0)
            cm.Modifiers.AddModifiers(statusCode, data.Mods, Entity);
        return statusCode;
    }

    /// <summary>移除状态(原版 RemoveStatus:同步撤修饰)。</summary>
    public void RemoveStatus(ComponentManager cm, string statusCode)
    {
        if (!_active.TryGetValue(statusCode, out var status)) return;
        if (status.Mods.Count > 0)
            cm.Modifiers.RemoveAllModifiers(statusCode, Entity);
        _active.Remove(statusCode);
    }

    /// <summary>每 sim 回合驱动(dt=回合秒数)。周期伤害/捕获经 DelayedDamage
    /// (过抗性,同普通命中);时限到 → 移除。</summary>
    public void Tick(ComponentManager cm, float dt)
    {
        if (_active.Count == 0) return;
        float dtMs = dt * 1000f;
        // 快照 keys:执行中可能移除自身。
        var keys = new List<string>(_active.Keys);
        foreach (var code in keys)
        {
            if (!_active.TryGetValue(code, out var status)) continue;
            float interval = status.IntervalMs > 0 ? status.IntervalMs : DefaultIntervalMs;
            bool hasEffect = !status.Damage.IsEmpty || status.Capture > Maths.Fixed.Zero;
            // 纯修饰(无周期效果亦无时限)→ 不走表,修饰由 Add/RemoveStatus 管理。
            if (!hasEffect && status.DurationMs <= 0) continue;

            status.SinceLastExecMs += dtMs;
            if (status.SinceLastExecMs < interval) continue;
            status.SinceLastExecMs -= interval;

            if (hasEffect)
            {
                DelayedDamage.ScheduleHit(cm, status.SourceEntity, Entity,
                    new DamageBlock { Amounts = new Dictionary<DamageType, int>(status.Damage.Amounts),
                        Capture = status.Capture }, delayTurns: 0);
            }

            if (status.DurationMs <= 0) continue;
            // 原版 ExecuteEffect 的时长账:_firstTime 首火不计,之后每火 += interval。
            if (status.FirstTime) status.FirstTime = false;
            else status.TimeElapsedMs += interval;
            if (status.TimeElapsedMs >= status.DurationMs)
                RemoveStatus(cm, code);
        }
    }

    public override void Serialize(ISerializer s)
    {
        // 状态表:key 排序(字典序)保确定性逐位一致。
        var keys = new List<string>(_active.Keys);
        keys.Sort(StringComparer.Ordinal);
        s.NumberI32("nse", keys.Count);
        foreach (var key in keys)
        {
            var st = _active[key];
            s.StringASCII("sk", key);
            s.StringASCII("sbase", st.BaseCode);
            s.NumberFixed("sdur", Maths.Fixed.FromFloat(st.DurationMs));
            s.NumberFixed("sint", Maths.Fixed.FromFloat(st.IntervalMs));
            st.Damage.Serialize(s, "sdmg");
            s.NumberFixed("scap", st.Capture);
            s.StringASCII("sstack", st.Stackability);
            s.NumberU32("ssrc", st.SourceEntity.Value);
            s.NumberI32("sown", st.SourceOwner);
            s.NumberFixed("stel", Maths.Fixed.FromFloat(st.TimeElapsedMs));
            s.NumberFixed("ssle", Maths.Fixed.FromFloat(st.SinceLastExecMs));
            s.Bool("sft", st.FirstTime);
            // 修饰:数量 + 逐条(路径/加/乘)。
            s.NumberI32("snmod", st.Mods.Count);
            foreach (var mod in st.Mods)
            {
                s.StringASCII("mpath", mod.Path ?? "");
                s.NumberFixed("madd", Maths.Fixed.FromFloat(mod.Add ?? 0f));
                s.NumberFixed("mmul", Maths.Fixed.FromFloat(mod.Multiply ?? 1f));
            }
        }
    }

    public override void Deserialize(IDeserializer d)
    {
        _active.Clear();
        int n = d.NumberI32("nse");
        for (int i = 0; i < n; i++)
        {
            string key = d.StringASCII("sk");
            var st = new ActiveStatusEffect
            {
                BaseCode = d.StringASCII("sbase"),
                DurationMs = d.NumberFixed("sdur").ToFloat(),
                IntervalMs = d.NumberFixed("sint").ToFloat(),
                Damage = DamageBlock.Deserialize(d, "sdmg"),
                Capture = d.NumberFixed("scap"),
                Stackability = d.StringASCII("sstack"),
                SourceEntity = new EntityId(d.NumberU32("ssrc")),
                SourceOwner = d.NumberI32("sown"),
                TimeElapsedMs = d.NumberFixed("stel").ToFloat(),
                SinceLastExecMs = d.NumberFixed("ssle").ToFloat(),
                FirstTime = d.Bool("sft"),
            };
            int nmod = d.NumberI32("snmod");
            for (int m = 0; m < nmod; m++)
            {
                string path = d.StringASCII("mpath");
                float add = d.NumberFixed("madd").ToFloat();
                float mul = d.NumberFixed("mmul").ToFloat();
                st.Mods.Add(new Modification(path, add, mul, null, System.Array.Empty<string>()));
            }
            _active[key] = st;
        }
    }

    public void HandleMessage(IMessage message) { }
}

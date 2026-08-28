using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

[Component("Health", "Health")]
public sealed class HealthComponent : ComponentBase, IComponentMessageHandler
{
    // 默认值活在字段初始化器上(不覆写 OnInit):`new HealthComponent { Current = 50 }` 的
    // 调用方在 AddComponent 后保值——此前 OnInit 无条件重置 100/100,静默吞掉所有
    // 指定值(EntityAssembler 的模板 HP 就中招)。同 OwnershipComponent 修复模式。
    public int Current = 100;
    public int Max = 100;
    /// <summary>模板 Health/Unhealable(原版 Heal.js CanHeal 检查):不可被治疗,只能修理。</summary>
    public bool Unhealable;
    /// <summary>模板基值(修正值管线的输入)。0 = 未显式设置,回退用 Max
    /// (既有创建点只管 Max,语义等价)。科技改变 Max 时由
    /// <see cref="ValueModificationApplier.RescaleHealth"/> 按比例缩放 Current。</summary>
    public int BaseMax;

    /// <summary>模板 Health/RegenRate(HP/秒;原版 Health.js Timer 每秒回复)。
    /// 建筑 template_structure 默认 5,单位基类 0(21 模板自定义)。</summary>
    public float RegenRate;
    /// <summary>模板 Health/IdleRegenRate(空闲单位额外回复;单位闲置时生效)。</summary>
    public float IdleRegenRate;
    // 小数回复结转(整型 HP 下 sub-1 再生量逐 tick 累积,同 Repairable 模式)。
    private float _regenCarry;

    /// <summary>每 tick 再生(原版 Health.js RegenTimer:hp += RegenRate[+Idle 若空闲];
    /// 空闲判定 = 无 UnitAI 当前攻击目标,近似原版 IsIdle)。</summary>
    public void TickRegen(ComponentManager cm, float dt)
    {
        if (IsDead || Current >= Max) { _regenCarry = 0; return; }
        float rate = RegenRate;
        if (rate <= 0 && IdleRegenRate <= 0) return;
        var ai = cm.QueryInterface<UnitAIComponent>(Entity);
        if (IdleRegenRate > 0 && ai is { IsIdle: true }) rate += IdleRegenRate;
        if (rate <= 0) return;
        _regenCarry += rate * dt;
        int whole = (int)MathF.Floor(_regenCarry);
        if (whole > 0)
        {
            _regenCarry -= whole;
            Heal(whole);
        }
    }

    /// <summary>修正值查询用的基值:BaseMax > 0 优先,否则 Max。</summary>
    public int BaseMaxOrMax => BaseMax > 0 ? BaseMax : Max;

    protected override void OnInit() { }

    public float HealthFraction => Max > 0 ? (float)Current / Max : 0f;

    /// <summary>原版 Health.js IsInjured:hp &lt; maxHp(Heal 的目标校验 + 补满即停判定)。</summary>
    public bool IsInjured => Current < Max;

    /// <summary>Apply a post-resistance damage block directly to health. This is the sink at the
    /// end of the Attack → DelayedDamage → Resistance → Health pipeline. Capture is handled
    /// separately (Capturable component) and ignored here.</summary>
    public void TakeDamage(DamageBlock damage)
    {
        Current = Math.Max(0, Current - damage.TotalPhysical);
    }

    /// <summary>Apply a flat amount of physical damage (post-resistance). Kept for back-compat
    /// with code paths that already computed the reduced value (e.g. tutorial scripting).</summary>
    public void TakeDamage(int amount)
    {
        Current = Math.Max(0, Current - amount);
    }

    public void Heal(int amount)
    {
        Current = Math.Min(Max, Current + amount);
    }

    public bool IsDead => Current <= 0;

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("cur", Current);
        s.NumberI32("max", Max);
        s.NumberI32("bmax", BaseMax);
        s.Bool("unhealable", Unhealable);
        s.NumberFixed("regen", Maths.Fixed.FromFloat(RegenRate));
        s.NumberFixed("iregen", Maths.Fixed.FromFloat(IdleRegenRate));
        s.NumberFixed("carry", Maths.Fixed.FromFloat(_regenCarry));
    }

    public override void Deserialize(IDeserializer d)
    {
        Current = d.NumberI32("cur");
        Max = d.NumberI32("max");
        BaseMax = d.NumberI32("bmax");
        Unhealable = d.Bool("unhealable");
        RegenRate = d.NumberFixed("regen").ToFloat();
        IdleRegenRate = d.NumberFixed("iregen").ToFloat();
        _regenCarry = d.NumberFixed("carry").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}

[Component("Attack", "Attack")]
public sealed class AttackComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>单个攻击型的完整参数(原版 Attack 组件的逐型 slot:Melee/Ranged/
    /// Slaughter 各自独立 Damage/Range/Rate/Restricted/Preferred)。原版一实体可
    /// Melee+Ranged 并存各用各的射程;此前合并为单字段,近战长枪兵会拿近战射程
    /// 硬打远程目标。</summary>
    public sealed class AttackTypeSpec
    {
        /// <summary>类型名:Melee / Ranged(Slaughter 解析期已剔除)。</summary>
        public string Name = "Melee";
        /// <summary>修正值路径段(原版 GetAttackEffectsData "Attack/{Name}/Damage/...")。</summary>
        public string ModifierPath => "Attack/" + Name + "/Damage/";
        public DamageBlock Damage = new();
        public float MaxRange = 3f;
        /// <summary>射速(次/秒;模板 RepeatTime 毫秒 → 1000/ms)。</summary>
        public float Rate = 1f;
        public string RestrictedClasses = "";
        public string PreferredClasses = "";
        /// <summary>攻击附带状态(原版逐型 ApplyStatus;空名 = 无)。</summary>
        public string StatusEffectName = "";
        public float StatusEffectDurationMs, StatusEffectIntervalMs;
        public string StatusEffectStackability = "Ignore";
        public int StatusEffectDmgHack, StatusEffectDmgPierce, StatusEffectDmgCrush, StatusEffectDmgFire;
        /// <summary>逐型溅射(范围伤害;0 = 无溅射;原版 Attack/*/Splash,圆形衰减)。</summary>
        public float SplashRange;
        public bool SplashFriendlyFire;
        public DamageBlock SplashDamage = new();

        public bool HasDamage => Damage.TotalPhysical > 0;

        public void Serialize(ISerializer s, string p)
        {
            s.StringASCII(p + "name", Name);
            Damage.Serialize(s, p + "dmg");
            s.NumberFixed(p + "range", Maths.Fixed.FromFloat(MaxRange));
            s.NumberFixed(p + "rate", Maths.Fixed.FromFloat(Rate));
            s.StringASCII(p + "restr", RestrictedClasses);
            s.StringASCII(p + "pref", PreferredClasses);
            s.StringASCII(p + "status", StatusEffectName);
            s.NumberFixed(p + "splash", Maths.Fixed.FromFloat(SplashRange));
            s.Bool(p + "ff", SplashFriendlyFire);
            SplashDamage.Serialize(s, p + "sdmg");
        }

        public static AttackTypeSpec Deserialize(IDeserializer d, string p)
        {
            var t = new AttackTypeSpec
            {
                Name = d.StringASCII(p + "name"),
                Damage = DamageBlock.Deserialize(d, p + "dmg"),
                MaxRange = d.NumberFixed(p + "range").ToFloat(),
                Rate = d.NumberFixed(p + "rate").ToFloat(),
                RestrictedClasses = d.StringASCII(p + "restr"),
                PreferredClasses = d.StringASCII(p + "pref"),
                StatusEffectName = d.StringASCII(p + "status"),
                SplashRange = d.NumberFixed(p + "splash").ToFloat(),
                SplashFriendlyFire = d.Bool(p + "ff"),
                SplashDamage = DamageBlock.Deserialize(d, p + "sdmg"),
            };
            return t;
        }
    }

    /// <summary>逐型列表(模板 Melee/Ranged 各一条;原版 Attack 组件同款)。</summary>
    public List<AttackTypeSpec> Types = new();
    /// <summary>当前目标选中的物理型(原版 order.data.attackType;Capture 路径不变)。
    /// 攻击中切换目标/射程不足时由 GetBestAttackAgainst 重选。</summary>
    public int CurrentTypeIndex = -1;

    /// <summary>当前选中的物理型(无 → 空对象,值为回退默认,仅供判读)。</summary>
    public AttackTypeSpec? CurrentPhysical =>
        CurrentTypeIndex >= 0 && CurrentTypeIndex < Types.Count ? Types[CurrentTypeIndex] : null;

    // ── 兼容面(旧调用方读单型;语义 = 当前选中型,未选则取首个有伤害型)──
    /// <summary>物理伤害块(选中型;未选 → 聚合各型最大)。getter 始终回引用
    /// (无伤害型时惰性建默认 Melee 型并置为选中)——保证 `Damage.Amounts[..]=x`
    /// 与 `Damage = block` 双路径都落地。setter 写入同一引用型。</summary>
    public DamageBlock Damage
    {
        get
        {
            var t = CurrentPhysical ?? Types.FirstOrDefault(x => x.HasDamage);
            if (t == null)
            {
                t = new AttackTypeSpec { Name = "Melee" };
                Types.Add(t);
                if (CurrentTypeIndex < 0) CurrentTypeIndex = Types.Count - 1;
            }
            return t.Damage;
        }
        set => Damage_Set(value);
    }

    private void Damage_Set(DamageBlock value)
    {
        var t = CurrentPhysical ?? Types.FirstOrDefault(x => x.HasDamage);
        if (t == null)
        {
            t = new AttackTypeSpec { Name = "Melee" };
            Types.Add(t);
            if (CurrentTypeIndex < 0) CurrentTypeIndex = Types.Count - 1;
        }
        t.Damage = value;
    }
    /// <summary>射程(选中型;未选 → 惰性建默认 Melee 型并置为选中,与 Damage 同款)。
    /// setter 写入同一引用型。</summary>
    public float Range
    {
        get
        {
            var t = CurrentPhysical ?? Types.FirstOrDefault(x => x.HasDamage);
            if (t == null)
            {
                t = new AttackTypeSpec { Name = "Melee" };
                Types.Add(t);
                if (CurrentTypeIndex < 0) CurrentTypeIndex = Types.Count - 1;
            }
            return t.MaxRange;
        }
        set => Range_Set(value);
    }

    private void Range_Set(float value)
    {
        var t = CurrentPhysical ?? Types.FirstOrDefault(x => x.HasDamage);
        if (t == null)
        {
            t = new AttackTypeSpec { Name = "Melee" };
            Types.Add(t);
            if (CurrentTypeIndex < 0) CurrentTypeIndex = Types.Count - 1;
        }
        t.MaxRange = value;
    }

    public float Rate
    {
        get
        {
            var t = CurrentPhysical ?? Types.FirstOrDefault(x => x.HasDamage);
            if (t == null) return 1f;
            return t.Rate;
        }
        set => Rate_Set(value);
    }

    private void Rate_Set(float value)
    {
        var t = CurrentPhysical ?? Types.FirstOrDefault(x => x.HasDamage);
        if (t == null)
        {
            t = new AttackTypeSpec { Name = "Melee" };
            Types.Add(t);
            if (CurrentTypeIndex < 0) CurrentTypeIndex = Types.Count - 1;
        }
        t.Rate = value;
    }

    public bool IsRanged
    {
        get => (CurrentPhysical ?? Types.FirstOrDefault(x => x.HasDamage))
            ?.Name == "Ranged";
        set
        {
            var t = CurrentPhysical ?? Types.FirstOrDefault(x => x.HasDamage);
            if (t == null)
            {
                t = new AttackTypeSpec { Name = "Melee" };
                Types.Add(t);
                if (CurrentTypeIndex < 0) CurrentTypeIndex = Types.Count - 1;
            }
            t.Name = value ? "Ranged" : "Melee";
        }
    }

    /// <summary>模板 Attack/Ranged/RangeOverlay 存在——选中时表现层画射程圈(对齐
    /// 原版 RangeOverlayManager;CC/箭塔有,近战无)。装配时写入。</summary>
    public bool HasRangeOverlay;
    public float Cooldown;
    public EntityId? Target;
    public AttackState State;

    // --- Capture 攻击类型(原版 Attack/Capture 顶层元素;CaptureStrength=0 = 无此类型) ---
    /// <summary>模板 Attack/Capture/Capture 值(小数:步兵 2.5/骑兵 1.75)。</summary>
    public Maths.Fixed CaptureStrength;
    public float CaptureRange = 4f;      // 模板 Attack/Capture/MaxRange
    public float CaptureRate = 1f;       // 模板 Attack/Capture/RepeatTime → 1000/ms
    /// <summary>模板 Attack/Capture/RestrictedClasses token 串(不可捕获的类别,
    /// 如 "Field Palisade Wall")。</summary>
    public string CaptureRestrictedClasses = "";
    /// <summary>物理类型(Melee|Ranged)RestrictedClasses token 串(原版逐型 CanAttack
    /// 门:命中即不可用此型攻击,如冲车 "Field Organic"、猎犬 "Structure Ship Siege")。
    /// setter 同步写入全部物理型(测试/旧码直设;逐型 RestrictedClasses 优先取型内)。</summary>
    public string PhysicalRestrictedClasses
    {
        get => _physRestr.Length > 0 ? _physRestr
            : Types.Count > 0 ? Types[0].RestrictedClasses : "";
        set
        {
            _physRestr = value;
            foreach (var t in Types) t.RestrictedClasses = value;
        }
    }
    private string _physRestr = "";
    /// <summary>物理类型(Melee|Ranged)PreferredClasses token 串
    /// (GetBestAttackAgainst 偏好 +2 判定,如 "Unit+!Ship")。setter 同步写入全部物理型。</summary>
    public string PreferredClasses
    {
        get => _prefCls.Length > 0 ? _prefCls
            : Types.Count > 0 ? Types[0].PreferredClasses : "";
        set
        {
            _prefCls = value;
            foreach (var t in Types) t.PreferredClasses = value;
        }
    }
    private string _prefCls = "";
    /// <summary>当前目标使用的攻击类型(true=Capture 型)。原版存 order.data.attackType
    /// (Order.Attack 时 GetBestAttackAgainst 选一次);我们挂组件,AttackTarget 重选,等价。</summary>
    public bool CurrentAttackIsCapture;

    // --- ApplyStatus(组件级兼容面;逐型数据在 AttackTypeSpec 里各有一份)---
    /// <summary>效果名(Burning/Poisoned;对应 data/status_effects/*.json code)。</summary>
    public string StatusEffectName = "";
    public float StatusEffectDurationMs;
    public float StatusEffectIntervalMs;
    public string StatusEffectStackability = "Ignore";
    public int StatusEffectDmgHack;
    public int StatusEffectDmgPierce;
    public int StatusEffectDmgCrush;
    public int StatusEffectDmgFire;

    public enum AttackState { Idle, Approaching, Attacking }

    /// <summary>攻击类型选择结果(原版 GetBestAttackAgainst 返回类型字符串;
    /// 我们只有物理(Melee|Ranged 合一)+ Capture 两型)。</summary>
    public enum AttackChoice { Physical, Capture }

    protected override void OnInit() { }

    /// <summary>选类型 + 锁定目标(对齐原版 Order.Attack:GetBestAttackAgainst 选不到
    /// (两门皆关)→ 拒单)。返回 false = 无任何可用攻击类型,调用方 FinishOrder。</summary>
    public bool AttackTarget(ComponentManager cm, EntityId target, bool allowCapture = false)
    {
        var choice = GetBestAttackAgainst(cm, target, allowCapture);
        if (choice == null) return false;
        CurrentAttackIsCapture = choice == AttackChoice.Capture;
        Target = target;
        State = AttackState.Approaching;
        return true;
    }

    /// <summary>Stop attacking and clear the current target. Called by UnitAI when an order
    /// finishes or the target is lost.</summary>
    public void StopAttacking()
    {
        Target = null;
        State = AttackState.Idle;
        Cooldown = 0;
    }

    /// <summary>对齐原版 Attack.GetBestAttackAgainst:各物理型(Melee/Ranged)+ Capture
    /// 全走 CanAttack 过滤,按偏好公式取最优(原版 sort 升序 .pop):pref = 型内
    /// PreferredClasses 命中(+2)+ 指令偏好(+1,allowCapture ? 捕获型 : 物理型);
    /// 得分 = 型序号 + (pref>0 ? pref+总数 : 0);型序 = Types 序(Melee 先 Ranged 后,
    /// Capture 恒排最后),平手归后者(升序 pop)。选不到 → null,并清 CurrentTypeIndex。</summary>
    public AttackChoice? GetBestAttackAgainst(ComponentManager cm, EntityId target, bool allowCapture)
    {
        var identity = cm.QueryInterface<IdentityComponent>(target);
        int nTypes = Types.Count + 1;   // 物理型数 + Capture
        int bestIdx = -1;
        int bestScore = int.MinValue;
        for (int i = 0; i < Types.Count; i++)
        {
            var t = Types[i];
            if (!CanAttackPhysical(cm, target, t)) continue;
            bool prefMatch = identity != null && t.PreferredClasses.Length > 0
                && Content.EntityClassHelper.MatchesClassList(identity.Classes, t.PreferredClasses);
            int pref = (prefMatch ? 2 : 0) + (allowCapture ? 0 : 1);
            int score = i + (pref > 0 ? pref + nTypes : 0);
            if (score >= bestScore) { bestScore = score; bestIdx = i; }
        }
        bool captureOk = CaptureStrength > Maths.Fixed.Zero && CanAttackCapture(cm, target);
        int capScore = int.MinValue;
        if (captureOk)
        {
            int pref = allowCapture ? 1 : 0;
            capScore = Types.Count + (pref > 0 ? pref + nTypes : 0);
        }
        CurrentTypeIndex = bestIdx;
        if (bestIdx < 0 && !captureOk) return null;
        return capScore >= bestScore && captureOk ? AttackChoice.Capture : AttackChoice.Physical;
    }

    /// <summary>原版 CanAttack 的物理型分支(逐型):外交敌对 + 目标有 Health 且 hp&gt;0
    /// + 该型 RestrictedClasses 不命中 + 该型确有伤害。记录在案:无 OwnershipComponent
    /// 的目标不拦(P0 旧语义,gaia 资源可打)。</summary>
    private bool CanAttackPhysical(ComponentManager cm, EntityId target, AttackTypeSpec type)
    {
        if (type.Damage.TotalPhysical <= 0) return false;
        var health = cm.QueryInterface<HealthComponent>(target);
        if (health == null || health.Current <= 0) return false;
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        var targetOwn = cm.QueryInterface<OwnershipComponent>(target);
        if (own != null && targetOwn != null
            && !cm.Players.IsEnemy(own.PlayerId, targetOwn.PlayerId))
            return false;
        if (type.RestrictedClasses.Length > 0)
        {
            var identity = cm.QueryInterface<IdentityComponent>(target);
            if (identity != null
                && Content.EntityClassHelper.MatchesClassList(identity.Classes, type.RestrictedClasses))
            {
                return false;
            }
        }
        // 组件级兼容面(测试/旧码直设 PhysicalRestrictedClasses):型内空时回落。
        else if (PhysicalRestrictedClasses.Length > 0)
        {
            var identity = cm.QueryInterface<IdentityComponent>(target);
            if (identity != null
                && Content.EntityClassHelper.MatchesClassList(identity.Classes, PhysicalRestrictedClasses))
            {
                return false;
            }
        }
        // 高度差门(原版 Attack.js CanAttack:|Δh| > 该型射程上限 → 永不可达)。
        if (!InHeightRange(cm, target, type.MaxRange)) return false;
        return true;
    }

    /// <summary>原版 CanAttack 的捕获型分支:目标可占领且 Capturable.CanCapture(我方)
    /// + RestrictedClasses 不命中。</summary>
    private bool CanAttackCapture(ComponentManager cm, EntityId target)
    {
        var capturable = cm.QueryInterface<CapturableComponent>(target);
        if (capturable == null) return false;
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        if (own == null || own.PlayerId < 0) return false;
        if (!capturable.CanCapture(cm, own.PlayerId)) return false;
        if (CaptureRestrictedClasses.Length > 0)
        {
            var identity = cm.QueryInterface<IdentityComponent>(target);
            if (identity != null
                && Content.EntityClassHelper.MatchesClassList(identity.Classes, CaptureRestrictedClasses))
                return false;
        }
        if (!InHeightRange(cm, target, CaptureRange)) return false;
        return true;
    }

    /// <summary>原版高度差判定:|双方 Y 差| ≤ range。无位置件/平地图(Y 恒 0)恒真。</summary>
    private bool InHeightRange(ComponentManager cm, EntityId target, float range)
    {
        var myPos = cm.QueryInterface<PositionComponent>(Entity);
        var targetPos = cm.QueryInterface<PositionComponent>(target);
        if (myPos == null || targetPos == null) return true;
        var dh = myPos.Position.Y - targetPos.Position.Y;
        if (dh < Maths.Fixed.Zero) dh = -dh;
        return dh <= Maths.Fixed.FromFloat(range);
    }

    /// <summary>攻击附带状态效果:选中物理型优先(原版逐型 ApplyStatus);型内无配置
    /// 时回落组件级字段(测试/旧码直设兼容面)。</summary>
    private StatusEffectSpec? BuildStatusSpec()
    {
        var t = CurrentPhysical;
        if (t != null && t.StatusEffectName.Length > 0)
            return new StatusEffectSpec(t.StatusEffectName, t.StatusEffectDurationMs,
                t.StatusEffectIntervalMs, t.StatusEffectStackability,
                t.StatusEffectDmgHack, t.StatusEffectDmgPierce,
                t.StatusEffectDmgCrush, t.StatusEffectDmgFire);
        if (StatusEffectName.Length == 0) return null;
        return new StatusEffectSpec(StatusEffectName, StatusEffectDurationMs,
            StatusEffectIntervalMs, StatusEffectStackability,
            StatusEffectDmgHack, StatusEffectDmgPierce,
            StatusEffectDmgCrush, StatusEffectDmgFire);
    }

    /// <summary>Perform one attack hit against the current target. Routes through DelayedDamage
    /// so resistance is applied and (for ranged) travel latency is honoured. Called by UnitAI's
    /// COMBAT.ATTACKING state on each attack cycle. Damage passes the modifier pipeline here
    /// (tech effects on Attack/{Melee|Ranged}/Damage/{type}), so research applies at hit time.
    /// 物理/捕获互斥(对齐原版:一次命中只用一种攻击类型,Capture 模板无 Damage 元素)。
    /// 逐型:修正值路径/伤害块/速率均取选中物理型。</summary>
    public void PerformAttack(ComponentManager cm)
    {
        if (Target == null) return;
        if (CurrentAttackIsCapture)
        {
            // 修正值路径对齐原版 GetAttackEffectsData("Attack/Capture"):Attack/Capture/Capture。
            float cap = cm.Modifiers.Apply("Attack/Capture/Capture", CaptureStrength.ToFloat(), Entity);
            DelayedDamage.ScheduleHit(cm, Entity, Target.Value,
                new DamageBlock { Capture = Maths.Fixed.FromFloat(cap) }, delaySeconds: 0f);
            Cooldown = 1.0f / CaptureRate;
            return;
        }
        var type = CurrentPhysical;
        if (type == null) return;
        string prefix = type.ModifierPath;
        var mod = new DamageBlock();
        foreach (var kv in type.Damage.Amounts.OrderBy(k => (int)k.Key)) // 排序保确定
            mod.Amounts[kv.Key] = (int)MathF.Round(
                cm.Modifiers.Apply(prefix + kv.Key, kv.Value, Entity), MidpointRounding.AwayFromZero);
        // 弹道延迟(原版 CCmpProjectileManager 连续飞行计时;0.01s 恒同拍落地——
        // PerformAttack 当拍 TickPending 即清,测试/玩法语义与原版一致;远程命中
        // 顺序仍按发射先后确定)。
        const float delay = 0.01f;
        DelayedDamage.ScheduleHit(cm, Entity, Target.Value, mod, delaySeconds: delay,
            status: BuildStatusSpec());
        // 溅射(原版 Attack/*/Splash:主目标全额,范围内其余按 (1-d²/r²) 衰减;
        // 投射体发射时即记,命中与主击同回合落地)。
        if (type.SplashRange > 0)
            ScheduleSplash(cm, type, delay);
        // 攻击发射事件（表现层生成飞行投射物）。纯视觉——伤害已由 DelayedDamage 排队结算。
        cm.Events.RaiseAttackLaunched(new Events.AttackLaunchedEvent
        { Attacker = Entity, Target = Target.Value, IsRanged = type.Name == "Ranged" });
        Cooldown = 1.0f / type.Rate;
    }

    /// <summary>溅射排期(原版 AttackHelper.CauseDamageOverArea 圆形衰减;主目标跳过——
    /// 已走主击全额,溅射只打范围内其余)。</summary>
    private void ScheduleSplash(ComponentManager cm, AttackTypeSpec type, float delay)
    {
        if (Target == null) return;
        var myPos = cm.QueryInterface<PositionComponent>(Entity);
        var targetPos = cm.QueryInterface<PositionComponent>(Target.Value);
        var range = SimSystem.Range;
        if (myPos == null || targetPos == null || range == null) return;

        int attackerOwner = cm.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1;
        float originX = targetPos.Position.X.ToFloat();
        float originZ = targetPos.Position.Z.ToFloat();
        float r = type.SplashRange;
        float r2 = r * r;

        foreach (var ent in range.ExecuteQuery(Target.Value, Maths.Fixed.Zero,
            Maths.Fixed.FromFloat(r)))
        {
            if (ent == Target.Value) continue;   // 主目标全额,溅射不重复
            if (!type.SplashFriendlyFire && attackerOwner >= 0)
            {
                var eOwner = cm.QueryInterface<OwnershipComponent>(ent)?.PlayerId ?? -1;
                if (eOwner == attackerOwner) continue;
                if (eOwner >= 0 && !cm.Players.IsEnemy(attackerOwner, eOwner)) continue;
            }
            var ePos = cm.QueryInterface<PositionComponent>(ent);
            if (ePos == null) continue;
            float dx = ePos.Position.X.ToFloat() - originX;
            float dz = ePos.Position.Z.ToFloat() - originZ;
            float d2 = dx * dx + dz * dz;
            if (d2 >= r2) continue;
            float mult = 1f - d2 / r2;   // 二次衰减(原版 Circular 同款)
            if (mult <= 0) continue;

            var splash = new DamageBlock();
            foreach (var kv in type.SplashDamage.Amounts.OrderBy(k => (int)k.Key))
            {
                float v = cm.Modifiers.Apply(
                    type.ModifierPath + kv.Key, kv.Value * mult, Entity);
                int iv = (int)MathF.Round(v, MidpointRounding.AwayFromZero);
                if (iv > 0) splash.Amounts[kv.Key] = iv;
            }
            if (splash.TotalPhysical > 0)
                DelayedDamage.ScheduleHit(cm, Entity, ent, splash, delaySeconds: delay);
        }
    }

    public void Tick(float dt, ComponentManager cm)
    {
        if (Target == null) return;
        if (Cooldown > 0) Cooldown -= dt;

        var targetHealth = cm.QueryInterface<HealthComponent>(Target.Value);
        if (targetHealth == null || targetHealth.IsDead)
        {
            StopAttacking();
            return;
        }

        // 翻面/外交变化重门(原版 OnOwnershipChanged/OnDiplomacyChanged 触发 UnitAI
        // 重评;我们 Tick 轮询等价):捕获型目标已不再可捕获(翻面完成/CP 抽干)→ 收工;
        // 物理型目标变非敌(被我方占领/外交变化)→ 收工,不再打自己的建筑。
        if (CurrentAttackIsCapture)
        {
            var capturable = cm.QueryInterface<CapturableComponent>(Target.Value);
            var myOwn = cm.QueryInterface<OwnershipComponent>(Entity);
            if (capturable == null || myOwn == null || !capturable.CanCapture(cm, myOwn.PlayerId))
            {
                StopAttacking();
                return;
            }
        }
        else if (CurrentPhysical == null || !CanAttackPhysical(cm, Target.Value, CurrentPhysical!))
        {
            StopAttacking();
            return;
        }

        var myPos = cm.QueryInterface<PositionComponent>(Entity);
        var targetPos = cm.QueryInterface<PositionComponent>(Target.Value);
        if (myPos == null || targetPos == null) return;

        float dx = targetPos.Position.X.ToFloat() - myPos.Position.X.ToFloat();
        float dz = targetPos.Position.Z.ToFloat() - myPos.Position.Z.ToFloat();
        float dist = MathF.Sqrt(dx * dx + dz * dz);

        var motion = cm.QueryInterface<UnitMotion>(Entity);
        // 射程取选中型(近战长枪兵不再拿近战射程硬打远程目标)。
        float range = CurrentAttackIsCapture ? CaptureRange
            : CurrentPhysical?.MaxRange ?? 3f;

        if (dist > range)
        {
            State = AttackState.Approaching;
            if (motion != null && !motion.HasMoveTarget)
            {
                motion.MoveToPoint(new Maths.FixedVector2D(
                    targetPos.Position.X, targetPos.Position.Z));
            }
        }
        else
        {
            State = AttackState.Attacking;
            if (motion != null) motion.Stop();

            if (Cooldown <= 0)
                PerformAttack(cm);
        }
    }

    public override void Serialize(ISerializer s)
    {
        // 兼容旧存档:首个字段仍为选中/默认型的聚合(Damage/Range/Rate/IsRanged)。
        // 读端对无 Types 块的旧档据此重建单型列表(语义 = 合并旧档)。
        Damage.Serialize(s, "dmg");
        s.NumberFixed("range", Maths.Fixed.FromFloat(Range));
        s.NumberFixed("rate", Maths.Fixed.FromFloat(Rate));
        s.NumberI32("state", (int)State);
        s.NumberU32("target", Target?.Value ?? 0);
        s.Bool("ranged", IsRanged);
        // Capture 攻击类型(本存档周期追加,读序须与写序逐位一致)。
        s.NumberFixed("capstr", CaptureStrength);
        s.NumberFixed("caprange", Maths.Fixed.FromFloat(CaptureRange));
        s.NumberFixed("caprate", Maths.Fixed.FromFloat(CaptureRate));
        s.StringASCII("caprestr", CaptureRestrictedClasses);
        s.StringASCII("prefcls", PreferredClasses);
        s.Bool("curcap", CurrentAttackIsCapture);
        s.StringASCII("physrestr", PhysicalRestrictedClasses);
        // 逐型列表(本周期新增;NTYPES=0 → 旧档,读端走重建)。
        s.NumberI32("ntypes", Types.Count);
        for (int i = 0; i < Types.Count; i++)
            Types[i].Serialize(s, $"t{i}.");
        s.NumberI32("curtype", CurrentTypeIndex);
    }

    public override void Deserialize(IDeserializer d)
    {
        var dmg = DamageBlock.Deserialize(d, "dmg");
        float range = d.NumberFixed("range").ToFloat();
        float rate = d.NumberFixed("rate").ToFloat();
        State = (AttackState)d.NumberI32("state");
        uint tid = d.NumberU32("target");
        Target = tid != 0 ? new EntityId(tid) : null;
        bool ranged = d.Bool("ranged");
        CaptureStrength = d.NumberFixed("capstr");
        CaptureRange = d.NumberFixed("caprange").ToFloat();
        CaptureRate = d.NumberFixed("caprate").ToFloat();
        CaptureRestrictedClasses = d.StringASCII("caprestr");
        // 组件级后备字段(兼容 setter 先行赋值的路径;新档 types 块优先覆盖)。
        _prefCls = d.StringASCII("prefcls");
        CurrentAttackIsCapture = d.Bool("curcap");
        _physRestr = d.StringASCII("physrestr");
        // 逐型块(新档);旧档(NTYPES=0)由聚合字段重建单型。
        int nTypes = d.NumberI32("ntypes");
        Types.Clear();
        for (int i = 0; i < nTypes; i++)
            Types.Add(AttackTypeSpec.Deserialize(d, $"t{i}."));
        CurrentTypeIndex = d.NumberI32("curtype");
        if (nTypes == 0)
        {
            // 旧档重建:单型(范围/速率/伤害 = 聚合;限制/偏好取组件级后备)。
            Types.Add(new AttackTypeSpec
            {
                Name = ranged ? "Ranged" : "Melee",
                Damage = dmg,
                MaxRange = range,
                Rate = rate,
                RestrictedClasses = _physRestr,
                PreferredClasses = _prefCls,
            });
            CurrentTypeIndex = dmg.TotalPhysical > 0 ? 0 : -1;
        }
        else
        {
            // 新档:型内空时回填组件级后备(逐型优先语义)。
            foreach (var t in Types)
            {
                if (t.RestrictedClasses.Length == 0) t.RestrictedClasses = _physRestr;
                if (t.PreferredClasses.Length == 0) t.PreferredClasses = _prefCls;
            }
        }
    }

    public void HandleMessage(IMessage message) { }
}

using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// Formation — port of binaries/.../simulation/components/Formation.js (编队控制器组件)。
// 编队控制器是虚拟实体(special/formations/* 模板):无 Health/Cost/Obstruction,不可被选
// 中/被攻击。它持有成员列表,按队形参数计算每成员偏移(ComputeFormationOffsets),通过
// UnitAI 的 FormationWalk 指令把成员布到"控制器位置+旋转后的偏移"上;成员在
// FORMATIONMEMBER.WALKING 中逐回合跟踪(原版 UnitMotion.MoveToFormationOffset)。
//
// 对齐原版的要点:
//  - 类组合:SortingClasses 按 "|" 分层,每层补 "" 占位,笛卡尔积 "+" 连接,末尾
//    "Unsorted" 兜底;成员取首个 MatchesClassList 命中的组合(最具体优先)。
//  - 布局:square/triangle 行布局,每行左右交替自中心向外,零均值居中;分隔 = 成员
//    平均 footprint(Obstruction 半径×2)× UnitSeparationWidth/DepthMultiplier。
//  - 分配:按组合逆优先级 splice(-n) 取偏移段(最具体的组合最后分,拿列表前部=前排),
//    段内 TakeClosestOffset 最近空位。
//  - 速度:ComputeMotionParameters = 最慢成员速度 × SpeedMultiplier(原版经
//    SetSpeedMultiplier 达成同值;我们直接设绝对速度,见方法注)。
//  - 生命周期:RemoveMembers 低于 RequiredMemberCount → Disband(毁控制器实体)。
//
// 已对齐但不移植/简化(记录在案的分歧):
//  - scatter(special)队形、TwinFormations 合并定时器、编队光环(formationMembersWithAura)、
//    编队作战(FormationAttack/CanAttackAsFormation)、AnimationVariants/memberPositions、
//    LoadFormation 换模板、IsRearrangementAllowed 的作战态闸门(UpdateFormation 直接放行)、
//    成员 Obstruction ControlGroup 穿越(我们的 Obstruction 不换控制组)。
//  - MaxColumnsUsed 原版是 1 索引数组(maxColumnsUsed[r]=n);我们用 0 索引 List,内容一致。
[Component("Formation", "Formation")]
public sealed class FormationComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>A member's assigned place: offset relative to the controller (pre-rotation),
    /// plus its 1-based row/column for animation-variant logic (deferred).</summary>
    public struct FormationOffset
    {
        public EntityId Ent;
        public float X, Z;
        public int Row, Column;
    }

    // --- 模板配置(来自 special/formations/* 模板;随状态序列化,读档不依赖模板重载) ---
    public int RequiredMemberCount = 2;
    public float SpeedMultiplier = 1f;
    public string Shape = "square";                       // FormationShape:square/triangle(special 未移植)
    public float MaxTurningAngle = 1f;                    // 弧度;转角超过则作废偏移重排
    public readonly List<string> SortingClasses = new();  // 每元素一层("Melee Ranged")
    public string SortingOrder = "";                      // ""/fillFromTheSides/fillToTheCenter
    public bool ShiftRows;
    public float UnitSeparationWidthMultiplier = 1f;
    public float UnitSeparationDepthMultiplier = 1f;
    public float Sloppiness;                              // 偏移抖动幅度(0=严格格点)
    public float WidthDepthRatio = 1f;
    public int MinColumns, MaxColumns, MaxRows;           // 0 = 不限
    public float CenterGap;
    public float FormationSeparation;                     // 双编队合并距离(合并逻辑未移植,仅存)
    /// <summary>模板 FormationAttack/CanAttackAsFormation:编队可否整体作战
    /// (phalanx/syntagma 类 true——成员原地作战、控制器留场计时;false——接敌即
    /// 成员散开各自为战,控制器移出世界等待)。</summary>
    public bool CanAttackAsFormation;

    // --- 运行态 ---
    public readonly List<EntityId> Members = new();
    /// <summary>带编队光环的成员(原版 formationMembersWithAura;其光环
    /// 只施加于编队成员,离队/解散摘除)。</summary>
    private readonly List<EntityId> _membersWithAura = new();
    public readonly List<EntityId> FinishedEntities = new();  // 已到位的成员(原版 Set;我们 List+去重)
    public readonly List<EntityId> TwinFormations = new();    // 同批分簇编队(原版 twinFormations)
    public int MaxRowsUsed;
    public readonly List<int> MaxColumnsUsed = new();         // 每行实际列数
    public float Width, Depth;                                // 最近一次重排的偏移包围盒
    public List<FormationOffset>? Offsets;                    // null = 需重算(成员变动/大转角)

    // 派生缓存(不序列化):类组合全集与逐成员匹配结果,均由 SortingClasses/成员类确定推出。
    private List<string>? _allMatching;
    private readonly Dictionary<EntityId, string> _classCache = new();
    private bool _disbanding;   // Disband→RemoveMembers 重入保护(原版靠空列表早退+引擎幂等双毁)

    private const string UnsortedClassCombination = "Unsorted";
    // 原版 g_RotateDistanceThreshold(平方距离,≈1m):目标近于此不转向。
    private const float RotateDistanceThreshold = 1f;

    public int GetMemberCount() => Members.Count;
    public float GetSpeedMultiplier() => SpeedMultiplier;

    private List<string> AllMatching =>
        _allMatching ??= GenerateAllMatchingClassCombinations(SortingClasses);

    // =========================================================================
    // 类组合(原版 GenerateAllMatchingClassCombinations / GetMemberClassCombinations)
    // =========================================================================

    /// <summary>Port of GenerateAllMatchingClassCombinations:每层末尾加 "" 占位后做笛卡尔积
    /// (reduce 顺序:首层变化最慢),"+“ 连接;空输入仅返回 [Unsorted]。</summary>
    public static List<string> GenerateAllMatchingClassCombinations(IReadOnlyList<string> sortingClassLevels)
    {
        if (sortingClassLevels == null || sortingClassLevels.Count == 0)
            return new List<string> { UnsortedClassCombination };

        var levels = new List<string[]>();
        foreach (var levelText in sortingClassLevels)
        {
            var tokens = levelText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var level = new List<string>();
            foreach (var t in tokens)
                if (t != UnsortedClassCombination) level.Add(t);
            if (level.Count == 0) continue;
            level.Add("");   // 占位:允许该层无类匹配(部分匹配而不落 Unsorted)
            levels.Add(level.ToArray());
        }
        if (levels.Count == 0)
            return new List<string> { UnsortedClassCombination };

        // cartesianProduct(utility.js):acc.flatMap(a => curr.map(c => [...a, c]))。
        var acc = new List<string[]> { Array.Empty<string>() };
        foreach (var level in levels)
        {
            var next = new List<string[]>(acc.Count * level.Length);
            foreach (var a in acc)
                foreach (var c in level)
                {
                    var combo = new string[a.Length + 1];
                    Array.Copy(a, combo, a.Length);
                    combo[a.Length] = c;
                    next.Add(combo);
                }
            acc = next;
        }

        var result = new List<string>(acc.Count + 1);
        foreach (var combo in acc)
            result.Add(string.Join('+', combo.Where(s => s.Length > 0)));
        result.Add(UnsortedClassCombination);
        return result;
    }

    /// <summary>Port of GetMemberClassCombinations:返回全集中首个 MatchesClassList 命中的
    /// 组合(全集有序 = 最具体优先),无命中兜底 Unsorted。结果按成员缓存(成员类不变)。</summary>
    public string GetMemberClassCombinations(ComponentManager cm, EntityId ent)
    {
        if (_classCache.TryGetValue(ent, out var cached))
            return cached;

        var classes = cm.QueryInterface<IdentityComponent>(ent)?.Classes
            ?? (IReadOnlyList<string>)Array.Empty<string>();
        string matched = UnsortedClassCombination;
        foreach (var combo in AllMatching)
        {
            if (combo.Length == 0) continue;   // 空组合永不匹配(原版 MatchesClassList 同),仅作占位
            if (Content.EntityClassHelper.MatchesClassList(classes, combo))
            {
                matched = combo;
                break;
            }
        }
        _classCache[ent] = matched;
        return matched;
    }

    // =========================================================================
    // 成员管理(原版 SetMembers / AddMembers / RemoveMembers / Disband)
    // =========================================================================

    /// <summary>Port of SetMembers:绑定成员→控制器链接,控制器移到成员质心,重算速度。
    /// 只应在创建时调一次。编队光环(ApplyFormationAura)未移植。</summary>
    public void SetMembers(ComponentManager cm, List<EntityId> ents)
    {
        Members.Clear();
        Members.AddRange(ents);
        foreach (var ent in Members)
        {
            cm.QueryInterface<UnitAIComponent>(ent)?.SetFormationController(Entity);
            // 编队光环(原版 SetMembers 段):带光环成员入列并对全队应用。
            var auras = cm.QueryInterface<AuraComponent>(ent);
            if (auras != null && cm.Auras != null && auras.HasFormationAura(cm.Auras))
            {
                if (!_membersWithAura.Contains(ent)) _membersWithAura.Add(ent);
                auras.ApplyFormationAura(cm, cm.Auras, Members);
            }
        }
        Offsets = null;
        MoveToMembersCenter(cm);
        ComputeMotionParameters(cm);
    }

    /// <summary>Port of AddMembers:追加成员并绑定链接;无订单的成员切到
    /// FORMATIONMEMBER.IDLE(原版 cmpUnitAI.SetNextState;我们的空闲单位不处理
    /// Timer,FsmNextState 无 drain 时机 → UnitAI 直接切换,见该处注释)。</summary>
    public void AddMembers(ComponentManager cm, List<EntityId> ents, bool renamed = false)
    {
        if (!renamed) Offsets = null;
        // 原版 AddMembers:已有光环成员对新队员重应用,新队员带光环则入列+应用。
        if (cm.Auras != null)
        {
            foreach (var bearer in _membersWithAura)
                cm.QueryInterface<AuraComponent>(bearer)
                    ?.ApplyFormationAura(cm, cm.Auras, ents);
        }
        Members.AddRange(ents);
        foreach (var ent in ents)
        {
            var ai = cm.QueryInterface<UnitAIComponent>(ent);
            if (ai != null)
            {
                ai.SetFormationController(Entity);
                ai.EnterFormationMemberIdleIfIdle();
            }
            var auras = cm.QueryInterface<AuraComponent>(ent);
            if (auras != null && cm.Auras != null && auras.HasFormationAura(cm.Auras))
            {
                if (!_membersWithAura.Contains(ent)) _membersWithAura.Add(ent);
                auras.ApplyFormationAura(cm, cm.Auras, Members);
            }
        }
        ComputeMotionParameters(cm);
    }

    /// <summary>Port of RemoveMembers:解除链接(成员 UnitAI 收 FormationLeave 消息),
    /// 低于 RequiredMemberCount → Disband。renamed=true(实体改名路径)不解散,本移植
    /// 仅供 Disband 内部重入使用(原版 UpdateWorkOrders 调用无对应物,略)。</summary>
    public void RemoveMembers(ComponentManager cm, List<EntityId> ents, bool renamed = false)
    {
        if (ents.Count == 0) return;
        if (!renamed) Offsets = null;
        // 原版 RemoveMembers:先对离队者摘全员光环(离队即失效);
        // 光环携带者离队 → 其对全队的施加一并摘除。
        if (cm.Auras != null)
            foreach (var bearer in _membersWithAura)
                cm.QueryInterface<AuraComponent>(bearer)
                    ?.RemoveFormationAura(cm, cm.Auras, ents);
        Members.RemoveAll(ents.Contains);
        foreach (var ent in ents)
        {
            FinishedEntities.Remove(ent);
            cm.QueryInterface<UnitAIComponent>(ent)?.UnsetFormationController();
            _classCache.Remove(ent);
            _membersWithAura.Remove(ent);
        }
        if (renamed) return;
        if (Members.Count < RequiredMemberCount && !_disbanding)
        {
            Disband(cm);
            return;
        }
        ComputeMotionParameters(cm);
    }

    /// <summary>Port of Disband:移除全部成员并销毁控制器实体。原版 Disband→RemoveMembers
    /// 会因成员数 < Required 递归一次(空列表早退)再双 DestroyEntity(靠引擎幂等);
    /// 我们用 _disbanding 守卫去掉递归与双毁,语义一致。</summary>
    public void Disband(ComponentManager cm)
    {
        DeleteTwinFormations(cm);
        _disbanding = true;
        RemoveMembers(cm, new List<EntityId>(Members));
        _disbanding = false;
        cm.DestroyEntity(Entity);
    }

    // =========================================================================
    // 布阵(原版 ArrangeFormation / UpdateFormation / MoveToMembersCenter)
    // =========================================================================

    /// <summary>Port of UpdateFormation:原版有 IsRearrangementAllowed 闸门(控制器/成员
    /// 作战态阻止重排);我们的控制器只支持 Walk,成员只有 FormationWalk,闸门恒真——
    /// 作战态闸门随编队作战一起做。</summary>
    public void UpdateFormation(ComponentManager cm, bool moveCenter = false, bool force = false)
        => ArrangeFormation(cm, moveCenter, force, null);

    /// <summary>Port of ArrangeFormation:必要时控制器跳到成员质心并朝目标转向(转角过大
    /// 作废偏移);偏移缺失则重算;向每成员发 FormationWalk(force=替换队列,否则排尾),
    /// 并刷新 Width/Depth。offsetsChanged/variant(动画变体)不移植。</summary>
    public void ArrangeFormation(ComponentManager cm, bool moveCenter, bool force, string? variant)
    {
        if (Members.Count == 0) return;

        var active = new List<EntityId>();
        var positions = new List<FixedVector2D>();
        foreach (var ent in Members)
        {
            var pos = cm.QueryInterface<PositionComponent>(ent);
            if (pos == null || !pos.InWorld) continue;
            active.Add(ent);
            positions.Add(new FixedVector2D(pos.Position.X, pos.Position.Z));
        }

        var ctrlAI = cm.QueryInterface<UnitAIComponent>(Entity);
        var ctrlPos = cm.QueryInterface<PositionComponent>(Entity);
        if (ctrlPos != null && moveCenter)
        {
            var avg = Average(positions);
            // 原版取 GetTargetPositions()[0](当前指令目标);我们的近似:当前指令 Position。
            var target = ctrlAI?.CurrentOrder?.Position;
            float oldRotation = ctrlPos.Rotation.Y.ToFloat();
            float newRotation = oldRotation;
            if (target is { } t)
            {
                float tdx = t.X.ToFloat() - avg.X.ToFloat();
                float tdz = t.Y.ToFloat() - avg.Y.ToFloat();
                if (tdx * tdx + tdz * tdz > RotateDistanceThreshold)
                    // 定点 atan2(编队朝向进 sim;libm Atan2 跨平台低位不同 → 队形转向漂移)。
                    newRotation = Trig.Atan2Approx(
                        Maths.Fixed.FromFloat(tdx), Maths.Fixed.FromFloat(tdz)).ToFloat();   // 朝向前方 = (sin,cos),见 GetRealOffsetPositions
            }
            if (!DoesAngleDifferenceAllowTurning(newRotation, oldRotation))
                Offsets = null;
            SetupPositionAndHandleRotation(cm, avg.X.ToFloat(), avg.Y.ToFloat(), newRotation, forceRotation: true);
        }

        Offsets ??= ComputeFormationOffsets(cm, active, positions);

        if (force)
            ResetFinishedEntities();

        float xMax = 0, yMax = 0, xMin = 0, yMin = 0;
        foreach (var offset in Offsets)
        {
            var ai = cm.QueryInterface<UnitAIComponent>(offset.Ent);
            if (ai == null) continue;   // 原版 warn:编队成员必须有 UnitAI
            ai.FormationWalk(Entity, offset.X, offset.Z, queued: !force);
            xMax = MathF.Max(xMax, offset.X);
            yMax = MathF.Max(yMax, offset.Z);
            xMin = MathF.Min(xMin, offset.X);
            yMin = MathF.Min(yMin, offset.Z);
        }
        Width = xMax - xMin;
        Depth = yMax - yMin;
    }

    /// <summary>原版 RegisterTwinFormation:互登对方为孪生(分簇编队同批建立时)。
    /// 合并判定时只遍历孪生表(原版同款)。</summary>
    public void RegisterTwinFormation(ComponentManager cm, EntityId other)
    {
        var of = cm.QueryInterface<FormationComponent>(other);
        if (of == null || other == Entity) return;
        if (!TwinFormations.Contains(other)) TwinFormations.Add(other);
        if (!of.TwinFormations.Contains(Entity)) of.TwinFormations.Add(Entity);
    }

    /// <summary>原版 DeleteTwinFormations:解散时互摘。</summary>
    public void DeleteTwinFormations(ComponentManager cm)
    {
        foreach (var ent in TwinFormations)
            cm.QueryInterface<FormationComponent>(ent)?.TwinFormations.Remove(Entity);
        TwinFormations.Clear();
    }

    /// <summary>Port of UpdateTwinFormationsForMerge:行进中的编队与近旁同模板同主编队
    /// 合并(距离 < 双方半边长之和 + FormationSeparation);被吸收方空员解散。
    /// 每拍至多并一队;双方都行进时只在 id 小的一侧检查(防双向重复)。</summary>
    public void MergeTwinFormations(ComponentManager cm)
    {
        var ctrlAI = cm.QueryInterface<UnitAIComponent>(Entity);
        if (ctrlAI == null || ctrlAI.IsIdle) return;   // 原版:行进中才合并

        var myPos = cm.QueryInterface<PositionComponent>(Entity);
        var myIdent = cm.QueryInterface<IdentityComponent>(Entity);
        var myOwn = cm.QueryInterface<OwnershipComponent>(Entity);
        if (myPos == null || !myPos.InWorld || myIdent == null || myOwn == null) return;

        float myHalf = MathF.Max(Width, Depth) / 2f;
        float baseDist = myHalf + FormationSeparation;

        // 原版只遍历 twinFormations(同批分簇编队)——非孪生编队永不合并。
        foreach (var other in TwinFormations.ToList())
        {
            if (other == Entity) continue;
            var of = cm.QueryInterface<FormationComponent>(other);
            if (of == null) { TwinFormations.Remove(other); continue; }   // 死编队摘除(原版 splice)
            var oIdent = cm.QueryInterface<IdentityComponent>(other);
            if (oIdent == null || oIdent.TemplateName != myIdent.TemplateName) continue;
            var oOwn = cm.QueryInterface<OwnershipComponent>(other);
            if (oOwn == null || oOwn.PlayerId != myOwn.PlayerId) continue;
            var oAI = cm.QueryInterface<UnitAIComponent>(other);
            if (oAI != null && !oAI.IsIdle && other.Value <= Entity.Value) continue;
            var oPos = cm.QueryInterface<PositionComponent>(other);
            if (oPos == null || !oPos.InWorld) continue;

            float dx = myPos.Position.X.ToFloat() - oPos.Position.X.ToFloat();
            float dz = myPos.Position.Z.ToFloat() - oPos.Position.Z.ToFloat();
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            float minDist = baseDist + MathF.Max(of.Width, of.Depth) / 2f;
            if (minDist < dist) continue;

            // 吸收:对方成员并入本方(对方空员解散,原版 RemoveMembers 连锁)。
            var members = new List<EntityId>(of.Members);
            of.RemoveMembers(cm, members);
            AddMembers(cm, members, renamed: true);
            UpdateFormation(cm, moveCenter: true, force: true);
            break;
        }
    }

    /// <summary>Port of MoveToMembersCenter:控制器跳到成员质心,朝向取成员平均
    /// (非强制转向:已在世界内则保持原朝向)。</summary>
    public void MoveToMembersCenter(ComponentManager cm)
    {
        var positions = new List<FixedVector2D>();
        float rotations = 0;
        foreach (var ent in Members)
        {
            var pos = cm.QueryInterface<PositionComponent>(ent);
            if (pos == null || !pos.InWorld) continue;
            positions.Add(new FixedVector2D(pos.Position.X, pos.Position.Z));
            rotations += pos.Rotation.Y.ToFloat();
        }
        if (positions.Count == 0) return;
        var avg = Average(positions);
        SetupPositionAndHandleRotation(cm, avg.X.ToFloat(), avg.Y.ToFloat(),
            rotations / positions.Count, forceRotation: false);
    }

    /// <summary>Port of SetupPositionAndHandleRotation:JumpTo + 按需 TurnTo。
    /// 原版的 RangeManager "normal" 标志(控制器不进范围查询)未移植——控制器无
    /// Vision/Visibility/Health,对我们的查询管线无副作用。</summary>
    private void SetupPositionAndHandleRotation(ComponentManager cm, float x, float z, float rot, bool forceRotation)
    {
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        if (pos == null) return;
        bool wasInWorld = pos.InWorld;
        var old = new FixedVector2D(pos.Position.X, pos.Position.Z);
        pos.Position = new FixedVector3D(Fixed.FromFloat(x), pos.Position.Y, Fixed.FromFloat(z));
        cm.NotifyPositionChanged(Entity, old, new FixedVector2D(pos.Position.X, pos.Position.Z));
        if (!forceRotation && wasInWorld) return;
        pos.Rotation = new FixedVector3D(pos.Rotation.X, Fixed.FromFloat(rot), pos.Rotation.Z);
    }

    // =========================================================================
    // 偏移计算(原版 GetAvgFootprint / ComputeFormationOffsets / TakeClosestOffset /
    // GetRealOffsetPositions / DoesAngleDifferenceAllowTurning)
    // =========================================================================

    /// <summary>Port of GetAvgFootprint:成员 Obstruction 半径×2 的平均(Footprint 圆形
    /// 语义);无 Obstruction 的成员跳过,全无时回退 (1,1)。</summary>
    public (float w, float d) GetAvgFootprint(ComponentManager cm, List<EntityId> active)
    {
        float w = 0, d = 0;
        int n = 0;
        foreach (var ent in active)
        {
            var obs = cm.QueryInterface<ObstructionComponent>(ent);
            if (obs == null) continue;
            float size = obs.GetSize().ToFloat();
            w += size * 2;
            d += size * 2;
            n++;
        }
        if (n == 0) return (1f, 1f);
        return (w / n, d / n);
    }

    /// <summary>Port of ComputeFormationOffsets:square/triangle 行布局(每行自中心左右
    /// 交替),零均值居中,可选排序(fillFromTheSides/fillToTheCenter),再按类组合逆
    /// 优先级 splice 分配 + TakeClosestOffset。抖动 randFloat(-1,1)×Sloppiness 走
    /// cm.RNG(确定性);special/scatter 队形未移植。结果同时刷新
    /// MaxRowsUsed/MaxColumnsUsed。</summary>
    public List<FormationOffset> ComputeFormationOffsets(
        ComponentManager cm, List<EntityId> active, List<FixedVector2D> positions)
    {
        var (sepW, sepD) = GetAvgFootprint(cm, active);
        sepW *= UnitSeparationWidthMultiplier;
        sepD *= UnitSeparationDepthMultiplier;

        // 按类组合分组(全集有序,每组保成员进入顺序)。
        var classCombinations = new Dictionary<string, List<(EntityId Ent, FixedVector2D Pos)>>();
        foreach (var combo in AllMatching)
            classCombinations[combo] = new List<(EntityId, FixedVector2D)>();
        for (int i = 0; i < active.Count; i++)
        {
            string combo = GetMemberClassCombinations(cm, active[i]);
            classCombinations[combo].Add((active[i], positions[i]));
        }

        int count = active.Count;
        var offsets = new List<FormationOffset>();

        float depth = count > 0 ? MathF.Sqrt(count / WidthDepthRatio) : 0;
        if (MaxRows > 0 && depth > MaxRows)
            depth = MaxRows;
        int cols = depth > 0 ? (int)MathF.Ceiling(count / MathF.Ceiling(depth) + (ShiftRows ? 0.5f : 0)) : count;
        if (cols < MinColumns)
            cols = Math.Min(count, MinColumns);
        if (MaxColumns > 0 && cols > MaxColumns && MaxRows != depth)
            cols = MaxColumns;

        MaxColumnsUsed.Clear();
        MaxRowsUsed = 0;
        int r = 0;
        int left = count;
        if (Shape == "special")
        {
            // 原版 Formation.js special=Scatter:成员随机散开,宽度 = √count ×
            // (sepW+sepD) × 2.5(反攻城散布);偏移过同一零均值/排序/分配管线。
            float width = MathF.Sqrt(count) * (sepW + sepD) * 2.5f;
            for (int i = 0; i < count; i++)
            {
                offsets.Add(new FormationOffset
                {
                    X = (float)cm.RNG.NextDouble() * width,
                    Z = (float)cm.RNG.NextDouble() * width,
                    Row = 1,
                    Column = i + 1,
                });
            }
            MaxColumnsUsed.Add(count);
            MaxRowsUsed = 1;
        }
        while (Shape != "special" && left > 0)
        {
            float z = -r * sepD;
            int side = 1;
            int n;
            if (Shape == "triangle")
                n = ShiftRows ? r + 1 : r * 2 + 1;
            else // "square"
            {
                n = cols;
                if (ShiftRows) n -= r % 2;
            }
            if (!ShiftRows && n > left)
                n = left;
            for (int c = 0; c < n && left > 0; ++c)
            {
                side *= -1;
                float x;
                if (n % 2 == 0)
                    x = side * (c / 2 + 0.5f) * sepW;        // Math.floor(c/2)
                else
                    x = side * ((c + 1) / 2) * sepW;         // Math.ceil(c/2)
                if (CenterGap > 0)
                {
                    if (x == 0) continue;                    // 中缝:跳过中心位
                    x += side * CenterGap / 2f;
                }
                int column = (n + 1) / 2 + (c + 1) / 2 * side; // ceil(n/2) + ceil(c/2)*side
                float r1 = (float)(cm.RNG.NextDouble() * 2 - 1) * Sloppiness;
                float r2 = (float)(cm.RNG.NextDouble() * 2 - 1) * Sloppiness;
                offsets.Add(new FormationOffset
                {
                    X = x + r1,
                    Z = z + r2,
                    Row = r + 1,
                    Column = column,
                });
                left--;
            }
            ++r;
            MaxColumnsUsed.Add(n);   // 原版 maxColumnsUsed[r] = n(1 索引)
        }
        if (Shape != "special") MaxRowsUsed = r;

        // 零均值居中:编队围绕控制器位置,非零均值会让编队每次重排跳位。
        float avgX = 0, avgZ = 0;
        foreach (var o in offsets) { avgX += o.X; avgZ += o.Z; }
        if (offsets.Count > 0) { avgX /= offsets.Count; avgZ /= offsets.Count; }
        for (int i = 0; i < offsets.Count; i++)
        {
            var o = offsets[i];
            o.X -= avgX;
            o.Z -= avgZ;
            offsets[i] = o;
        }

        // 排序:列表前部留给"最重"的单位(排序语义 = 升序,前部=中央/侧边内侧)。
        // 原版 JS 比较器返回布尔(实现定义的次序);我们按注释意图排稳定升序。
        if (SortingOrder == "fillFromTheSides")
            offsets.Sort((a, b) => MathF.Abs(a.X).CompareTo(MathF.Abs(b.X)));
        else if (SortingOrder == "fillToTheCenter")
            offsets.Sort((a, b) =>
                MathF.Max(MathF.Abs(a.X), MathF.Abs(a.Z)).CompareTo(
                    MathF.Max(MathF.Abs(b.X), MathF.Abs(b.Z))));

        var realPositions = GetRealOffsetPositions(cm, offsets);

        // 真实感分配:按组合逆优先级 splice(-n)(Unsorted 先拿尾段,最具体的组合最后
        // 拿剩下的前部 = 前排),段内每成员取最近空位。
        var newOffsets = new List<FormationOffset>();
        var allMatching = AllMatching;
        for (int ci = allMatching.Count - 1; ci >= 0; ci--)
        {
            var t = classCombinations[allMatching[ci]];
            if (t.Count == 0) continue;
            var usedOffsets = offsets.GetRange(offsets.Count - t.Count, t.Count);
            offsets.RemoveRange(offsets.Count - t.Count, t.Count);
            var usedReal = realPositions.GetRange(realPositions.Count - t.Count, t.Count);
            realPositions.RemoveRange(realPositions.Count - t.Count, t.Count);
            foreach (var entPos in t)
            {
                int closestId = TakeClosestOffset(entPos.Pos, usedReal);
                var o = usedOffsets[closestId];
                usedReal.RemoveAt(closestId);
                usedOffsets.RemoveAt(closestId);
                o.Ent = entPos.Ent;
                newOffsets.Add(o);
            }
        }
        return newOffsets;
    }

    /// <summary>Port of TakeClosestOffset:返回 realPositions 中距 entPos 最近的索引
    /// (严格小于取先)。原版本处写 memberPositions(动画变体用),随变体一起未移植。</summary>
    private static int TakeClosestOffset(FixedVector2D entPos, List<(float X, float Z)> realPositions)
    {
        float px = entPos.X.ToFloat(), pz = entPos.Y.ToFloat();
        int closest = -1;
        float best = float.MaxValue;
        for (int i = 0; i < realPositions.Count; i++)
        {
            float dx = px - realPositions[i].X, dz = pz - realPositions[i].Z;
            float d2 = dx * dx + dz * dz;
            if (d2 < best)
            {
                best = d2;
                closest = i;
            }
        }
        return closest;
    }

    /// <summary>Port of GetRealOffsetPositions:偏移按控制器朝向旋转后落到世界坐标。
    /// x = pos.x + o.z·sin + o.x·cos,z = pos.z + o.z·cos − o.x·sin(与 Turret 同一旋转
    /// 约定;朝向前方映射为 (sin,cos))。</summary>
    public List<(float X, float Z)> GetRealOffsetPositions(ComponentManager cm, List<FormationOffset> offsets)
    {
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        float px = pos?.Position.X.ToFloat() ?? 0;
        float pz = pos?.Position.Z.ToFloat() ?? 0;
        float rot = pos?.Rotation.Y.ToFloat() ?? 0;
        // 定点 sincos(编队成员世界偏移 = sim 位置;libm 三角跨平台漂移 → 队形散位 OOS)。
        Trig.SinCosApprox(Maths.Fixed.FromFloat(rot), out Maths.Fixed formSin, out Maths.Fixed formCos);
        float sin = formSin.ToFloat(), cos = formCos.ToFloat();
        var result = new List<(float X, float Z)>(offsets.Count);
        foreach (var o in offsets)
            result.Add((px + o.Z * sin + o.X * cos, pz + o.Z * cos - o.X * sin));
        return result;
    }

    /// <summary>Port of DoesAngleDifferenceAllowTurning:两角差(取模)小于
    /// MaxTurningAngle 时允许不重新分配位置地转向。</summary>
    public bool DoesAngleDifferenceAllowTurning(float a1, float a2)
    {
        float d = MathF.Abs(a1 - a2) % (2 * MathF.PI);
        return d < MaxTurningAngle || d > 2 * MathF.PI - MaxTurningAngle;
    }

    // =========================================================================
    // 速度(原版 ComputeMotionParameters)
    // =========================================================================

    /// <summary>Port of ComputeMotionParameters:控制器速度 = 最慢成员速度 ×
    /// SpeedMultiplier。原版经 SetSpeedMultiplier(minSpeed/控制器基速) 达成同值;我们
    /// 直接设绝对速度,效果一致且更直白。加速度/通行类别(Pathfinder clearance)无
    /// 对应物,不移植。成员速度取修正值管线后的有效值(原版 GetWalkSpeed 同)。</summary>
    public void ComputeMotionParameters(ComponentManager cm)
    {
        if (Members.Count == 0) return;
        float minSpeed = float.MaxValue;
        foreach (var ent in Members)
        {
            var motion = cm.QueryInterface<UnitMotion>(ent);
            if (motion == null) continue;
            float speed = cm.Modifiers.Apply("UnitMotion/WalkSpeed", motion.Speed.ToFloat(), ent);
            minSpeed = MathF.Min(minSpeed, speed);
        }
        if (minSpeed == float.MaxValue) return;
        minSpeed *= GetSpeedMultiplier();
        var ctrlMotion = cm.QueryInterface<UnitMotion>(Entity);
        if (ctrlMotion != null)
            ctrlMotion.Speed = Fixed.FromFloat(minSpeed);
    }

    // =========================================================================
    // 到位跟踪(原版 SetFinishedEntity 等)
    // =========================================================================

    /// <summary>Port of SetFinishedEntity:成员转正到编队朝向并标记到位(去重,
    /// 原版是 Set)。</summary>
    public void SetFinishedEntity(ComponentManager cm, EntityId ent)
    {
        var ctrlPos = cm.QueryInterface<PositionComponent>(Entity);
        var entPos = cm.QueryInterface<PositionComponent>(ent);
        if (entPos != null && entPos.InWorld && ctrlPos != null && ctrlPos.InWorld)
            entPos.Rotation = new FixedVector3D(entPos.Rotation.X, ctrlPos.Rotation.Y, entPos.Rotation.Z);
        if (!FinishedEntities.Contains(ent))
            FinishedEntities.Add(ent);
    }

    public void UnsetFinishedEntity(EntityId ent) => FinishedEntities.Remove(ent);
    public void ResetFinishedEntities() => FinishedEntities.Clear();
    public bool AreAllMembersFinished() => FinishedEntities.Count == Members.Count;

    private static FixedVector2D Average(List<FixedVector2D> positions)
    {
        float x = 0, z = 0;
        foreach (var p in positions) { x += p.X.ToFloat(); z += p.Y.ToFloat(); }
        if (positions.Count > 0) { x /= positions.Count; z /= positions.Count; }
        return new FixedVector2D(Fixed.FromFloat(x), Fixed.FromFloat(z));
    }

    // =========================================================================
    // 编队作战(原版 FormationAttack.js:GetRange 聚合 + GetClosestMemberToEntity)
    // =========================================================================

    /// <summary>Port of FormationAttack.GetRange:跨成员聚合对 target 的射程。
    /// CanAttackAsFormation → 取成员最小 max(保证全员够得着);否则取最大 max
    /// (散开接敌的触发半径);min 恒取最小;最后加 Depth/2(队深折算前排距离)。
    /// 无任何可战成员 → max = CanAttackAsFormation ? -1 : 0(+Depth/2 仅 max≥0 时)。</summary>
    public (float Min, float Max) GetAttackRange(ComponentManager cm, EntityId target)
    {
        float min = 0f;
        float max = CanAttackAsFormation ? -1f : 0f;
        foreach (var ent in Members)
        {
            var atk = cm.QueryInterface<AttackComponent>(ent);
            if (atk == null) continue;
            var choice = atk.GetBestAttackAgainst(cm, target, allowCapture: false);
            if (choice == null) continue;
            float rmax = choice == AttackComponent.AttackChoice.Capture ? atk.CaptureRange : atk.Range;
            const float rmin = 0f;   // 我们的 AttackComponent 无 MinRange(原版模板多为 0)
            if (CanAttackAsFormation)
            {
                if (rmax < max || max < 0) max = rmax;
            }
            else if (rmax > max || max < 0) max = rmax;
            if (rmin < min) min = rmin;
        }
        if (max >= 0) max += Depth / 2f;
        return (min, max);
    }

    /// <summary>Port of GetClosestMemberToEntity:距 ent 最近的在世成员(无 → null)。
    /// 原版有 filter 参数(本移植的调用点都不用)。</summary>
    public EntityId? GetClosestMemberToEntity(ComponentManager cm, EntityId ent)
    {
        var pos = cm.QueryInterface<PositionComponent>(ent);
        if (pos == null || !pos.InWorld) return null;
        float px = pos.Position.X.ToFloat(), pz = pos.Position.Z.ToFloat();
        EntityId? best = null;
        float bestD2 = float.MaxValue;
        foreach (var member in Members)
        {
            var mp = cm.QueryInterface<PositionComponent>(member);
            if (mp == null || !mp.InWorld) continue;
            float dx = mp.Position.X.ToFloat() - px, dz = mp.Position.Z.ToFloat() - pz;
            float d2 = dx * dx + dz * dz;
            if (d2 < bestD2) { bestD2 = d2; best = member; }
        }
        return best;
    }

    // =========================================================================
    // 序列化。桩阶段没有任何实体挂过本组件 → 无存档兼容包袱,字段顺序按新布局定义。
    // =========================================================================

    public override void Serialize(ISerializer s)
    {
        s.StringASCII("shape", Shape);
        SerializeEntityList(s, "members", Members);
        SerializeEntityList(s, "finished", FinishedEntities);
        SerializeEntityList(s, "twins", TwinFormations);
        s.NumberI32("sorting_n", SortingClasses.Count);
        foreach (var cls in SortingClasses) s.StringASCII("sorting", cls);
        s.NumberI32("maxRows", MaxRowsUsed);
        s.NumberI32("maxCols_n", MaxColumnsUsed.Count);
        foreach (var c in MaxColumnsUsed) s.NumberI32("maxCols", c);
        s.NumberFixed("width", Fixed.FromFloat(Width));
        s.NumberFixed("depth", Fixed.FromFloat(Depth));
        s.NumberFixed("separation", Fixed.FromFloat(FormationSeparation));
        // 模板配置(读档不重新解析模板,全部随状态走)。
        s.NumberI32("required", RequiredMemberCount);
        s.NumberFixed("speedMult", Fixed.FromFloat(SpeedMultiplier));
        s.NumberFixed("maxTurning", Fixed.FromFloat(MaxTurningAngle));
        s.StringASCII("sortingOrder", SortingOrder);
        s.Bool("shiftRows", ShiftRows);
        s.NumberFixed("sepW", Fixed.FromFloat(UnitSeparationWidthMultiplier));
        s.NumberFixed("sepD", Fixed.FromFloat(UnitSeparationDepthMultiplier));
        s.NumberFixed("sloppiness", Fixed.FromFloat(Sloppiness));
        s.NumberFixed("wdRatio", Fixed.FromFloat(WidthDepthRatio));
        s.NumberI32("minCols", MinColumns);
        s.NumberI32("maxColsCfg", MaxColumns);
        s.NumberI32("maxRowsCfg", MaxRows);
        s.NumberFixed("centerGap", Fixed.FromFloat(CenterGap));
        s.Bool("canAtkFormation", CanAttackAsFormation);
        // 偏移(原版同样序列化 offsets;读档后无需重算,RNG 流不偏移)。
        s.Bool("hasOffsets", Offsets != null);
        if (Offsets != null)
        {
            s.NumberI32("offsets_n", Offsets.Count);
            foreach (var o in Offsets)
            {
                s.NumberU32("o_ent", o.Ent.Value);
                s.NumberFixed("o_x", Fixed.FromFloat(o.X));
                s.NumberFixed("o_z", Fixed.FromFloat(o.Z));
                s.NumberI32("o_row", o.Row);
                s.NumberI32("o_col", o.Column);
            }
        }
    }

    public override void Deserialize(IDeserializer d)
    {
        Shape = d.StringASCII("shape");
        DeserializeEntityList(d, "members", Members);
        DeserializeEntityList(d, "finished", FinishedEntities);
        DeserializeEntityList(d, "twins", TwinFormations);
        SortingClasses.Clear();
        int sn = d.NumberI32("sorting_n");
        for (int i = 0; i < sn; i++) SortingClasses.Add(d.StringASCII("sorting"));
        MaxRowsUsed = d.NumberI32("maxRows");
        MaxColumnsUsed.Clear();
        int mcn = d.NumberI32("maxCols_n");
        for (int i = 0; i < mcn; i++) MaxColumnsUsed.Add(d.NumberI32("maxCols"));
        Width = d.NumberFixed("width").ToFloat();
        Depth = d.NumberFixed("depth").ToFloat();
        FormationSeparation = d.NumberFixed("separation").ToFloat();
        RequiredMemberCount = d.NumberI32("required");
        SpeedMultiplier = d.NumberFixed("speedMult").ToFloat();
        MaxTurningAngle = d.NumberFixed("maxTurning").ToFloat();
        SortingOrder = d.StringASCII("sortingOrder");
        ShiftRows = d.Bool("shiftRows");
        UnitSeparationWidthMultiplier = d.NumberFixed("sepW").ToFloat();
        UnitSeparationDepthMultiplier = d.NumberFixed("sepD").ToFloat();
        Sloppiness = d.NumberFixed("sloppiness").ToFloat();
        WidthDepthRatio = d.NumberFixed("wdRatio").ToFloat();
        MinColumns = d.NumberI32("minCols");
        MaxColumns = d.NumberI32("maxColsCfg");
        MaxRows = d.NumberI32("maxRowsCfg");
        CenterGap = d.NumberFixed("centerGap").ToFloat();
        CanAttackAsFormation = d.Bool("canAtkFormation");
        if (d.Bool("hasOffsets"))
        {
            int n = d.NumberI32("offsets_n");
            Offsets = new List<FormationOffset>(n);
            for (int i = 0; i < n; i++)
            {
                Offsets.Add(new FormationOffset
                {
                    Ent = new EntityId(d.NumberU32("o_ent")),
                    X = d.NumberFixed("o_x").ToFloat(),
                    Z = d.NumberFixed("o_z").ToFloat(),
                    Row = d.NumberI32("o_row"),
                    Column = d.NumberI32("o_col"),
                });
            }
        }
        else
        {
            Offsets = null;
        }
        _allMatching = null;   // 派生缓存按新 SortingClasses 惰性重建
        _classCache.Clear();
    }

    private static void SerializeEntityList(ISerializer s, string prefix, List<EntityId> list)
    {
        s.NumberI32(prefix + "_n", list.Count);
        foreach (var e in list) s.NumberU32(prefix, e.Value);
    }

    private static void DeserializeEntityList(IDeserializer d, string prefix, List<EntityId> list)
    {
        list.Clear();
        int n = d.NumberI32(prefix + "_n");
        for (int i = 0; i < n; i++) list.Add(new EntityId(d.NumberU32(prefix)));
    }

    public void HandleMessage(IMessage message) { }
}

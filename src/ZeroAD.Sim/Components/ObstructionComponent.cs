using System;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components
{
    /// <summary>Obstruction shape kind, mirroring <c>ICmpObstruction::EObstructionType</c>.</summary>
    public enum ObstructionType { Unit, Static }

    /// <summary>
    /// Per-entity obstruction: registers a shape (circle for units, rotated rectangle for
    /// buildings) with the system <see cref="ObstructionManager"/> so other entities route around
    /// it, can't be placed on top of it, and can detect collision with it. Ported from
    /// <c>CCmpObstruction</c>.
    ///
    /// Lifecycle mirrors the original: shape is added when the entity enters the world (OnInit,
    /// after Position is set) and removed on teardown (OnDeinit). Position changes are tracked via
    /// <see cref="ComponentManager.PositionChanged"/> so the shape follows the entity — this fixes
    /// the legacy bug where dead buildings left their obstruction on the grid forever, because the
    /// grid was only ever mutated by SimBridge.BlockCircle and never cleared on death.
    /// </summary>
    [Component("Obstruction", "Obstruction")]
    public sealed class ObstructionComponent : ComponentBase, IComponentMessageHandler
    {
        public ObstructionType Type = ObstructionType.Unit;
        // Static: Size0=width, Size1=depth (full, not half). Unit: Size0=clearance (radius).
        public Fixed Size0 = Fixed.FromInt(1);
        public Fixed Size1 = Fixed.FromInt(1);
        public ObstructionFlags Flags = ObstructionFlags.DefaultBlock;
        public uint ControlGroup;       // 0 = self (default assigned in OnInit)
        public uint ControlGroup2;      // 0 = none
        public bool Active = true;
        /// <summary>原版 DisableBlockMovement/DisableBlockPathfinding(门自动开关的核心):
        /// 覆盖式禁用——注册形状用 EffectiveFlags(基旗减去禁用项);模板可预置
        /// (Obstruction 子元素),运行时由 GateComponent 等切换。</summary>
        public bool DisableBlockMovement;
        public bool DisableBlockPathfinding;

        /// <summary>实际生效旗标(原版查询时的 disable 覆盖语义)。</summary>
        public ObstructionFlags EffectiveFlags()
        {
            var f = Flags;
            if (DisableBlockMovement) f &= ~ObstructionFlags.BlockMovement;
            if (DisableBlockPathfinding) f &= ~ObstructionFlags.BlockPathfinding;
            return f;
        }

        /// <summary>原版 ICmpObstruction::SetDisableBlockMovementPathfinding:
        /// 切换禁用并强制形状重注册(重注册走 EffectiveFlags + 自动打脏寻路网格)。</summary>
        public void SetDisableBlockMovementPathfinding(bool disableMovement, bool disablePathfinding)
        {
            if (DisableBlockMovement == disableMovement && DisableBlockPathfinding == disablePathfinding)
                return;
            DisableBlockMovement = disableMovement;
            DisableBlockPathfinding = disablePathfinding;
            if (!_registered) return;
            // 重挂形状让 EffectiveFlags 生效(打脏由 manager Remove/Add 自动完成)。
            SimSystem.Obstructions?.RemoveShape(_tag);
            _registered = false;
            _tag = default;
            EnsureRegistered();
        }

        /// <summary>同玩家墙体共用的控制组(高位命名空间,撞不了实体 id):同组墙件
        /// 互不阻挡——原版墙体拼链(段搭进塔楼)依赖此。Placement 校验同组豁免。</summary>
        public static uint PlayerWallGroup(int playerId) => 0x40000000u | (uint)playerId;

        private ObstructionTag _tag;
        private bool _registered;
        // Track last-known position so we can forward the old XZ on a PositionChanged notification.
        private FixedVector2D _lastPos;

        /// <summary>The shape handle once registered (invalid before EnsureRegistered). Exposed so
        /// callers like Footprint.PickSpawnPoint can skip this entity's own obstruction when
        /// searching for a spawn slot just outside it.</summary>
        public ObstructionTag Tag => _tag;

        protected override void OnInit()
        {
            // Default control group = self, so an entity never blocks itself and adjacent walls of
            // the same group don't mutually block (matches the original's m_ControlGroup default).
            if (ControlGroup == 0)
                ControlGroup = Entity.Value;
        }

        /// <summary>
        /// Register the shape with the ObstructionManager. Call after the PositionComponent is set
        /// (SimBridge ensures this in its spawn order). Idempotent — safe to call if already
        /// registered. Returns false (and skips registration) if there is no PositionComponent or
        /// no ObstructionManager wired up.
        /// </summary>
        public bool EnsureRegistered()
        {
            if (_registered || !Active) return _registered;
            var mgr = SimSystem.Obstructions;
            if (mgr == null) return false;
            var pos = SimSystem.GetComponent<PositionComponent>(Entity);
            if (pos == null) return false;

            _lastPos = new FixedVector2D(pos.Position.X, pos.Position.Z);
            FixedVector2D u = new(Fixed.FromInt(1), Fixed.Zero);
            FixedVector2D v = new(Fixed.Zero, Fixed.FromInt(1));

            if (Type == ObstructionType.Static)
            {
                Fixed hw = Size0 / Fixed.FromInt(2);
                Fixed hh = Size1 / Fixed.FromInt(2);
                _tag = mgr.AddStaticShape(Entity, _lastPos.X, _lastPos.Y, u, v, hw, hh, EffectiveFlags(), ControlGroup, ControlGroup2);
            }
            else
            {
                _tag = mgr.AddUnitShape(Entity, _lastPos.X, _lastPos.Y, Size0, EffectiveFlags(), ControlGroup);
            }
            _registered = true;
            // Follow this entity's moves so the shape tracks it (units walk, buildings rotate).
            if (SimSystem.Sim is { } cm)
                cm.PositionChanged += OnPositionChanged;
            return true;
        }

        /// <summary>原版 ICmpObstruction::SetControlGroup:切换控制组(已注册形状同步到
        /// manager;未注册只更新字段,EnsureRegistered 时带上)。</summary>
        public void SetControlGroup(uint group)
        {
            if (ControlGroup == group) return;
            ControlGroup = group;
            if (_registered)
                SimSystem.Obstructions?.SetControlGroup(_tag, group);
        }

        /// <summary>Port of CCmpObstruction::SetActive: deactivate drops the registered shape
        /// (turreted units stop blocking pathing); reactivate re-registers. No-op when the
        /// manager isn't wired (kernel tests) — the flag still flips.</summary>
        public void SetActive(bool active)
        {
            if (Active == active) return;
            Active = active;
            if (!active)
            {
                if (!_registered) return;
                if (SimSystem.Sim != null)
                    SimSystem.Sim.PositionChanged -= OnPositionChanged;
                SimSystem.Obstructions?.RemoveShape(_tag);
                _tag = default;
                _registered = false;
            }
            else
            {
                EnsureRegistered();
            }
        }

        private void OnPositionChanged(EntityId entity, FixedVector2D from, FixedVector2D to)
        {
            if (entity != Entity || !_registered) return;
            var mgr = SimSystem.Obstructions;
            if (mgr == null) return;
            // Units move; static shapes rarely do (only on rotation/territory drag). Forward both.
            if (Type == ObstructionType.Unit)
                mgr.MoveUnitShape(_tag, to.X, to.Y);
            _lastPos = to;
        }

        /// <summary>
        /// Foundation placement check (simplified). Tests whether this entity's footprint overlaps
        /// any other foundation-blocking obstruction. Mirrors <c>CCmpObstruction::CheckFoundation</c>
        /// minus the pathfinder's terrain-passability step (handled separately by Pathfinder).
        /// Returns a result code matching the original's <c>EFoundationCheck</c>.
        /// </summary>
        public FoundationCheck CheckFoundation(string passClass)
        {
            var mgr = SimSystem.Obstructions;
            var pos = SimSystem.GetComponent<PositionComponent>(Entity);
            if (mgr == null || pos == null || !_registered) return FoundationCheck.FailNoObstruction;

            // Filter: skip shapes in our own control group, and only count shapes that block
            // foundation placement. Matches SkipControlGroupsRequireFlagObstructionFilter.
            ObstructionShapeFilter filter = (tag, flags, group, group2) =>
                group == ControlGroup || group2 == ControlGroup ||
                (flags & ObstructionFlags.BlockFoundation) == 0;

            FixedVector2D u = new(Fixed.FromInt(1), Fixed.Zero);
            FixedVector2D v = new(Fixed.Zero, Fixed.FromInt(1));
            if (Type == ObstructionType.Static)
            {
                Fixed hw = Size0 / Fixed.FromInt(2);
                Fixed hh = Size1 / Fixed.FromInt(2);
                var hits = mgr.TestStaticShape(filter, pos.Position.X, pos.Position.Z, u, v, hw, hh);
                return hits.Count == 0 ? FoundationCheck.Success : FoundationCheck.FailObstructsFoundation;
            }
            else
            {
                var hits = mgr.TestUnitShape(filter, pos.Position.X, pos.Position.Z, Size0);
                return hits.Count == 0 ? FoundationCheck.Success : FoundationCheck.FailObstructsFoundation;
            }
        }

        /// <summary>Return the obstruction as a world-space square (for range queries / debug).</summary>
        public ObstructionSquare? GetObstructionSquare()
        {
            if (!_registered) return null;
            return SimSystem.Obstructions?.GetObstruction(_tag);
        }

        public Fixed GetSize()
        {
            // Rough "radius" for range queries: unit clearance, or static half-diagonal.
            if (Type == ObstructionType.Unit) return Size0;
            Fixed hw = Size0 / Fixed.FromInt(2);
            Fixed hh = Size1 / Fixed.FromInt(2);
            // half-diagonal = sqrt(hw² + hh²) — use integer sqrt for determinism.
            long sq = (long)hw.InternalValue * hw.InternalValue + (long)hh.InternalValue * hh.InternalValue;
            return Fixed.Zero.WithInternalValue((int)MathInt.Sqrt64((ulong)sq));
        }

        protected override void OnDeinit()
        {
            // Tear down: remove the shape so the obstruction doesn't outlive the entity. This is
            // the fix for the legacy "dead buildings keep blocking the grid" bug.
            if (SimSystem.Sim is { } cm)
                cm.PositionChanged -= OnPositionChanged;
            if (_registered)
            {
                SimSystem.Obstructions?.RemoveShape(_tag);
                _registered = false;
                _tag = default;
            }
        }

        public override void Serialize(ISerializer s)
        {
            s.NumberI32("type", (int)Type);
            s.NumberFixed("s0", Size0);
            s.NumberFixed("s1", Size1);
            s.NumberU8("flags", (byte)Flags);
            s.NumberU32("grp", ControlGroup);
            s.NumberU32("grp2", ControlGroup2);
            s.Bool("active", Active);
            s.Bool("dbm", DisableBlockMovement);     // 存档 v14
            s.Bool("dbp", DisableBlockPathfinding);
        }

        public override void Deserialize(IDeserializer d)
        {
            Type = (ObstructionType)d.NumberI32("type");
            Size0 = d.NumberFixed("s0");
            Size1 = d.NumberFixed("s1");
            Flags = (ObstructionFlags)d.NumberU8("flags");
            ControlGroup = d.NumberU32("grp");
            ControlGroup2 = d.NumberU32("grp2");
            Active = d.Bool("active");
            DisableBlockMovement = d.Bool("dbm");
            DisableBlockPathfinding = d.Bool("dbp");
        }

        public void HandleMessage(IMessage message) { }
    }

    /// <summary>Foundation placement result codes, mirroring <c>ICmpObstruction::EFoundationCheck</c>.</summary>
    public enum FoundationCheck
    {
        Success,
        FailError,
        FailNoObstruction,
        FailObstructsFoundation,
        FailTerrainClass
    }
}

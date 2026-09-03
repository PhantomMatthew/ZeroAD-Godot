using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components
{
    /// <summary>城门组件(原版 Gate.js 全量移植):锁态 + 自动开关 + 阻挡联动。
    /// 原版语义:
    ///  - 盟友(互盟+自己)带 UnitAI 的可动单位进入 PassRange(模板,默认 20m)
    ///    → 自动开门(锁定门不开);范围内无此类单位 → 关门;
    ///  - 关门前置:门洞无 BlockConstruction 实体(有则保持开,下拍重试——
    ///    原版 0-time 定时器重试,我们由 TickGates 节拍天然重试);
    ///  - 阻挡旗:开门 → DisableBlockMovement+Pathfinding 双禁;
    ///    关门(未锁) → 仅恢复 BlockMovement(pathfinding 仍放行——未锁门可被
    ///    寻路穿过,反正能再开);锁定且关 → 双恢复;
    ///  - 打包中的攻城器(Pack 组件 Packing/Packed)不撑门(原版 AbleToMove 忽略表
    ///    的近似:AbleToMove 含打包外更多态,记录在案)。
    /// 序列化 Locked/Opened——读档恢复;阻挡禁用态由 EnsureRegistered 的
    /// EffectiveFlags 重建。</summary>
    public sealed class GateComponent : ComponentBase
    {
        /// <summary>上锁 = 阻挡通行(true);原版默认未锁(false,可通行)。</summary>
        public bool Locked;
        /// <summary>当前开着(自动开关状态机;玩家看不到的瞬时态也序列化——
        /// 读档须恢复阻挡禁用,否则门视觉上开着但实际挡路)。</summary>
        public bool Opened;
        /// <summary>开门感应半径(模板 Gate/PassRange,默认 20m)。</summary>
        public float PassRange = 20f;

        /// <summary>盟友单位在感应范围内 → 应开(原版 ShouldOpen:忽略表外任一盟友)。</summary>
        public bool ShouldOpen(ComponentManager cm)
        {
            var own = cm.QueryInterface<OwnershipComponent>(Entity);
            var range = SimSystem.Range;
            if (own == null || range == null) return false;
            var allies = cm.Players.GetMutualAllies(own.PlayerId);
            var found = range.ExecuteQuery(Entity, Fixed.Zero, Fixed.FromFloat(PassRange), eid =>
            {
                var eo = cm.QueryInterface<OwnershipComponent>(eid);
                if (eo == null) return false;
                if (eo.PlayerId != own.PlayerId && !allies.Contains(eo.PlayerId)) return false;
                var ai = cm.QueryInterface<UnitAIComponent>(eid);
                if (ai == null) return false;
                // 原版 ignoreList = !AbleToMove(打包中的攻城器等不撑门)。
                var pack = cm.QueryInterface<PackComponent>(eid);
                if (pack != null && (pack.Packed || pack.Packing)) return false;
                return true;
            });
            return found.Count > 0;
        }

        /// <summary>原版 OperateGate(由 TickGates 节拍与锁态切换调用):
        /// 开且(锁或不应开)→ 关;关且应开 → 开(OpenGate 内部查锁)。</summary>
        public void OperateGate(ComponentManager cm)
        {
            if (Opened && (Locked || !ShouldOpen(cm)))
                CloseGate(cm);
            else if (!Opened && ShouldOpen(cm))
                OpenGate(cm);
        }

        /// <summary>原版 OpenGate:锁定不开;双禁阻挡 + Opened=true。</summary>
        public void OpenGate(ComponentManager cm)
        {
            if (Locked) return;
            cm.QueryInterface<ObstructionComponent>(Entity)
                ?.SetDisableBlockMovementPathfinding(true, true);
            Opened = true;
        }

        /// <summary>原版 CloseGate:门洞被 BlockConstruction 实体占用 → 保持开
        /// (下拍重试);否则恢复阻挡(锁定 → 双恢复;未锁 → 只恢复移动阻挡)。</summary>
        public void CloseGate(ComponentManager cm)
        {
            var obstruction = cm.QueryInterface<ObstructionComponent>(Entity);
            if (obstruction != null && SimSystem.Obstructions != null
                && SimSystem.Obstructions.GetEntitiesBlockingConstruction(obstruction.Tag).Count > 0)
                return;   // 门洞有物 —— 保持开,下拍重试(原版 0-time 定时器同款)
            obstruction?.SetDisableBlockMovementPathfinding(false, !Locked);
            Opened = false;
        }

        /// <summary>切换锁态(原版 LockGate/UnlockGate;GUI 门面板按钮经此):</summary>
        public void SetLocked(ComponentManager cm, bool locked)
        {
            if (Locked == locked) return;
            Locked = locked;
            var obstruction = cm.QueryInterface<ObstructionComponent>(Entity);
            if (locked)
            {
                // LockGate:门关着 → 立即双恢复;开着 → OperateGate 收(locked 使关)。
                if (!Opened)
                    obstruction?.SetDisableBlockMovementPathfinding(false, false);
                else
                    OperateGate(cm);
            }
            else
            {
                // UnlockGate:pathfinding 放行(开门则移动也放);关着 → 视情况开。
                obstruction?.SetDisableBlockMovementPathfinding(Opened, true);
                if (!Opened)
                    OperateGate(cm);
            }
        }

        public override void Serialize(ISerializer s)
        {
            s.Bool("locked", Locked);
            s.Bool("opened", Opened);
        }

        public override void Deserialize(IDeserializer d)
        {
            Locked = d.Bool("locked");
            Opened = d.Bool("opened");
            // 阻挡禁用态按锁/开态重建(读档路径不经 SetLocked/OperateGate)。
            var obstruction = SimSystem.GetComponent<ObstructionComponent>(Entity);
            if (obstruction != null)
            {
                if (Opened) obstruction.SetDisableBlockMovementPathfinding(true, true);
                else if (Locked) obstruction.SetDisableBlockMovementPathfinding(false, false);
                else obstruction.SetDisableBlockMovementPathfinding(false, true);
            }
        }
    }
}

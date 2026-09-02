using System;
using System.Collections.Generic;
using System.IO;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Net
{
    public enum NetCommandType : byte
    {
        Invalid = 0,
        Move = 1,
        Gather = 2,
        Attack = 3,
        Build = 4,
        Train = 5,
        Research = 6,
        SetRallyPoint = 7,
        // 第二梯队菜单面板:外交/贸易。载荷复用现有固定字段(见各工厂文档)。
        SetStance = 8,
        Tribute = 9,
        SetTradingGoods = 10,
        Barter = 11,
        // 会话内指令栏:Stop(EntityId=单位)/ Delete(EntityId=己方实体)/ CancelProduction
        // (EntityId=生产建筑,IntParam1=队列下标)。
        Stop = 12,
        Delete = 13,
        CancelProduction = 14,
        // 会话内:SetUnitStance(EntityId=单位,TemplateName=站姿名)/ Garrison(EntityId=单位,
        // IntParam1=宿主)/ Ungarrison(EntityId=宿主,IntParam1=要卸载的实体,-1=全部)。
        SetUnitStance = 15,
        Garrison = 16,
        Ungarrison = 17,
        // Phase 4 缺口：Petra entity.js/common-api 用的命令（逐字移植优先级最高的 3 个）。
        Repair = 18,           // builder 修复/建造地基（EntityId=builder, IntParam1=target）
        ReturnResource = 19,   // gatherer 返回资源到投放站（EntityId=gatherer, IntParam1=dropsite）
        AttackWalk = 20,       // 攻击移动（EntityId=单位组首, FixedParam1/2=x/z 目标）
        WalkToRange = 21,      // 移动到攻击范围（EntityId=单位, IntParam1=target, FixedParam1/2=min/maxRange）
        SetupTradeRoute = 22,  // 建立贸易路线（EntityId=trader, IntParam1=targetMarket, 第一市场=EntityId）
        CollectTreasure = 23,  // 收集宝藏（EntityId=collector, IntParam1=treasure）
        Guard = 24,            // 护卫（EntityId=guard, IntParam1=target）
        Patrol = 25,           // 巡逻（EntityId=单位, FixedParam1/2=x/z 目标点）
        /// <summary>Formation: 编队创建/解散。TemplateName 载荷 "shape|id1,id2,..."
        /// (shape=null → 解散所列成员的控制器;否则创建 special/formations/{shape} 控制器)。
        /// 原版 cmd {type:"formation", entities, name}。</summary>
        Formation = 26,
        /// <summary>Pack: 攻城器打包/解包。EntityId=单位, IntParam1: 0=pack, 1=unpack。
        /// 原版 cmd {type:"pack"/"unpack"}。</summary>
        Pack = 27,
        /// <summary>Upgrade: 建筑升级(哨塔→防御塔等)。EntityId=建筑, IntParam1=建造者实体。
        /// 原版 cmd {type:"upgrade", entities}。</summary>
        Upgrade = 28,
        /// <summary>Gate: 城门锁切换。EntityId=城门, IntParam1: 0=解锁(通行), 1=上锁(阻挡)。
        /// 原版 cmd {type:"lock-gate"/"unlock-gate"}。</summary>
        Gate = 29,
        /// <summary>请求盟友进攻某敌(原版 chat attack-request;锁步命令,
        /// 执行器广播 AttackRequestedEvent → AI attackManager 评估)。</summary>
        AttackRequest = 30,
    }

    /// <summary>
    /// A player command travelling the lockstep network. Commands are the ONLY mutator of
    /// sim state in multiplayer: they are scheduled COMMAND_DELAY turns ahead, aggregated
    /// by the host into per-turn bundles, and applied by SimCommandExecutor at the same
    /// turn on every peer. The legacy TrainSoldier type was removed — Train carries the
    /// full template name and a count.
    /// </summary>
    public readonly struct NetCommand
    {
        public readonly uint Player;
        public readonly NetCommandType Type;
        public readonly uint EntityId;
        public readonly int IntParam1;
        public readonly int IntParam2;
        public readonly int FixedParam1;
        public readonly int FixedParam2;
        /// <summary>
        /// Template name for Train/Build (entity template) or Research (technology id).
        /// Carried with the command so every peer resolves the exact same data.
        /// </summary>
        public readonly string TemplateName;

        public NetCommand(uint player, NetCommandType type, uint entityId = 0,
            int p1 = 0, int p2 = 0, int fp1 = 0, int fp2 = 0, string? templateName = null)
        {
            Player = player; Type = type; EntityId = entityId;
            IntParam1 = p1; IntParam2 = p2; FixedParam1 = fp1; FixedParam2 = fp2;
            TemplateName = templateName ?? "";
        }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream(48);
            using var bw = new BinaryWriter(ms);
            bw.Write(Player);
            bw.Write((byte)Type);
            bw.Write(EntityId);
            bw.Write(IntParam1);
            bw.Write(IntParam2);
            bw.Write(FixedParam1);
            bw.Write(FixedParam2);
            byte[] tmplBytes = System.Text.Encoding.UTF8.GetBytes(TemplateName);
            bw.Write(tmplBytes.Length);
            bw.Write(tmplBytes);
            return ms.ToArray();
        }

        public static NetCommand Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            uint player = br.ReadUInt32();
            var type = (NetCommandType)br.ReadByte();
            uint entityId = br.ReadUInt32();
            int p1 = br.ReadInt32();
            int p2 = br.ReadInt32();
            int fp1 = br.ReadInt32();
            int fp2 = br.ReadInt32();
            // Matches Serialize: raw int32 byte count + raw UTF8 bytes (NOT ReadString,
            // which expects a 7-bit-encoded length prefix and would misalign the stream).
            int tmplLen = br.ReadInt32();
            string templateName = System.Text.Encoding.UTF8.GetString(br.ReadBytes(tmplLen));
            return new NetCommand(player, type, entityId, p1, p2, fp1, fp2, templateName);
        }

        /// <summary>Length-prefixed batch framing for per-turn bundles and client batches.</summary>
        public static byte[] SerializeBatch(IReadOnlyList<NetCommand> commands)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(commands.Count);
            foreach (var cmd in commands)
            {
                byte[] payload = cmd.Serialize();
                bw.Write(payload.Length);
                bw.Write(payload);
            }
            return ms.ToArray();
        }

        public static NetCommand[] DeserializeBatch(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            int count = br.ReadInt32();
            var commands = new NetCommand[count];
            for (int i = 0; i < count; i++)
            {
                int len = br.ReadInt32();
                commands[i] = Deserialize(br.ReadBytes(len));
            }
            return commands;
        }

        public static NetCommand Move(uint player, uint entityId, Fixed x, Fixed z) =>
            new(player, NetCommandType.Move, entityId, 0, 0, x.InternalValue, z.InternalValue);

        /// <summary>Gather: IntParam1 = target supply entity id.</summary>
        public static NetCommand Gather(uint player, uint unitId, uint targetId) =>
            new(player, NetCommandType.Gather, unitId, (int)targetId);

        /// <summary>Attack: IntParam1 = target entity id; IntParam2 = allowCapture (0/1,
        /// 原版 cmd.allowCapture,GUI Ctrl+攻击)。</summary>
        public static NetCommand Attack(uint player, uint attackerId, uint targetId, bool allowCapture = false) =>
            new(player, NetCommandType.Attack, attackerId, (int)targetId, allowCapture ? 1 : 0);

        /// <summary>Build: EntityId = builder, TemplateName = full building template,
        /// FixedParam1/2 = world x/z, IntParam1 = yaw angle (radians, Fixed.InternalValue;
        /// 原版 cmd.angle,对齐 placement.js DEFAULT_ANGLE=3π/4). Cost charge + foundation
        /// spawn happen at execution.</summary>
        public static NetCommand Build(uint player, uint builderId, string template, Fixed x, Fixed z, Fixed angle) =>
            new(player, NetCommandType.Build, builderId, angle.InternalValue, 0, x.InternalValue, z.InternalValue, template);

        /// <summary>Train: IntParam1 = count (batch training sends 5 as one command).</summary>
        public static NetCommand Train(uint player, uint buildingId, string templateName, int count = 1) =>
            new(player, NetCommandType.Train, buildingId, count, 0, 0, 0, templateName);

        /// <summary>Research: TemplateName = technology id.</summary>
        public static NetCommand Research(uint player, uint buildingId, string techName) =>
            new(player, NetCommandType.Research, buildingId, 0, 0, 0, 0, techName);

        /// <summary>SetRallyPoint: IntParam1 = target entity id (resource gather anchor).
        /// Use <see cref="SetRallyPointPosition"/> for a ground rally point; passing 0 with
        /// no FixedParam clears the rally point.</summary>
        public static NetCommand SetRallyPoint(uint player, uint buildingId, uint targetEntityId) =>
            new(player, NetCommandType.SetRallyPoint, buildingId, (int)targetEntityId);

        /// <summary>SetRallyPoint 全量版(原版 RallyPoint.AddPosition/AddData):
        /// commandType 集结点指令(walk/gather/repair/garrison/attack/patrol/trade…),
        /// resourceType 随 gather-near;append=true 追加到队列尾(原版 Shift+点击);
        /// false = 重置为单点。打包:TemplateName = "cmd;res",IntParam2 bit0 = append。</summary>
        public static NetCommand SetRallyPointFull(uint player, uint buildingId, uint targetEntityId,
            Fixed x, Fixed z, string commandType, string resourceType = "", bool append = false) =>
            new(player, NetCommandType.SetRallyPoint, buildingId, (int)targetEntityId,
                append ? 1 : 0, x.InternalValue, z.InternalValue,
                string.IsNullOrEmpty(resourceType) ? commandType : commandType + ";" + resourceType);

        /// <summary>SetRallyPoint on empty ground: IntParam1 = 0 (signals ground rally),
        /// FixedParam1/2 = world x/z as <see cref="Fixed.InternalValue"/>. Same type/enum as
        /// the entity variant, so lockstep bundling and save serialization are unchanged;
        /// execution distinguishes the two by IntParam1 (对齐原版"右键空地设集合点").</summary>
        public static NetCommand SetRallyPointPosition(uint player, uint buildingId, Fixed x, Fixed z) =>
            new(player, NetCommandType.SetRallyPoint, buildingId, 0, 0, x.InternalValue, z.InternalValue);

        /// <summary>SetStance(外交立场):IntParam1 = 目标玩家,IntParam2 = stance
        /// (DiplomacyComponent.Ally=1 / Neutral=0 / Enemy=-1)。原版 cmd
        /// {type:"diplomacy", player, to:"ally"|"neutral"|"enemy"}。执行时套单向恶化规则
        /// (我降立场则对方同步降),并对齐 ceasefire/teamLock 门(本轮恒放行)。</summary>
        public static NetCommand SetStance(uint player, int targetPlayer, int stance) =>
            new(player, NetCommandType.SetStance, 0, targetPlayer, stance);

        /// <summary>Tribute(进贡):IntParam1 = 收方玩家,IntParam2 = 数额,
        /// FixedParam1 = (int)ResourceType。原版 cmd {type:"tribute", player, amounts:{res:amt}}
        /// (单资源/次;Shift=500,默认 100)。</summary>
        public static NetCommand Tribute(uint player, int destPlayer, ResourceType type, int amount) =>
            new(player, NetCommandType.Tribute, 0, destPlayer, amount, (int)type);

        /// <summary>SetTradingGoods(贸易品比例):IntParam1=wood%, IntParam2=food%,
        /// FixedParam1=stone%, FixedParam2=metal%。4 值须 ≥0 且和=100(执行端校验)。
        /// 原版 cmd {type:"set-trading-goods", tradingGoods:{res:pct,...}}。</summary>
        public static NetCommand SetTradingGoods(uint player, int wood, int food, int stone, int metal) =>
            new(player, NetCommandType.SetTradingGoods, 0, wood, food, stone, metal);

        /// <summary>Barter(易物):IntParam1=(int)sellRes, IntParam2=(int)buyRes,
        /// FixedParam1=amount(100 或 500)。原版 cmd {type:"barter", sell, buy, amount}。
        /// 系统级易物(本轮去价漂移,价格静态 truePrice±CONSTANT_DIFFERENCE)。</summary>
        public static NetCommand Barter(uint player, ResourceType sell, ResourceType buy, int amount) =>
            new(player, NetCommandType.Barter, 0, (int)sell, (int)buy, amount);

        /// <summary>Stop:EntityId = 单位。清空订单回 IDLE(原版 "stop" 命令)。</summary>
        public static NetCommand Stop(uint player, uint unitId) =>
            new(player, NetCommandType.Stop, unitId);

        /// <summary>Delete:EntityId = 己方实体。执行端校验归属后销毁
        /// (原版 "delete-entities",本移植仅己方)。</summary>
        public static NetCommand Delete(uint player, uint entityId) =>
            new(player, NetCommandType.Delete, entityId);

        /// <summary>CancelProduction:EntityId = 生产建筑,IntParam1 = 队列下标。
        /// 取消并全额退资源(原版 "stop-production" + RemoveItem)。</summary>
        public static NetCommand CancelProduction(uint player, uint buildingId, int queueIndex) =>
            new(player, NetCommandType.CancelProduction, buildingId, queueIndex);

        /// <summary>SetUnitStance:EntityId = 单位,TemplateName = 站姿名
        /// (violent/aggressive/defensive/passive/standground)。原版 cmd {type:"stance", name}。</summary>
        public static NetCommand SetUnitStance(uint player, uint unitId, string stance) =>
            new(player, NetCommandType.SetUnitStance, unitId, templateName: stance);

        /// <summary>Garrison:EntityId = 单位,IntParam1 = 宿主实体(走 UnitAI Order.Garrison,
        /// 原版 cmd {type:"garrison", target})。</summary>
        public static NetCommand Garrison(uint player, uint unitId, uint holderId) =>
            new(player, NetCommandType.Garrison, unitId, (int)holderId);

        /// <summary>Ungarrison:EntityId = 宿主,IntParam1 = 要卸载的实体(-1 = 全部,
        /// 原版 "unload"/"unload-all-by-owner")。</summary>
        public static NetCommand Ungarrison(uint player, uint holderId, int unitId = -1) =>
            new(player, NetCommandType.Ungarrison, holderId, unitId);

        // ── Phase 4 缺口：Petra entity.js 用的命令工厂 ──

        /// <summary>Repair: builder 修复/建造地基。EntityId=builder, IntParam1=target foundation。
        /// 原版 cmd {type:"repair", target}。</summary>
        public static NetCommand Repair(uint player, uint builderId, uint targetId) =>
            new(player, NetCommandType.Repair, builderId, (int)targetId);

        /// <summary>ReturnResource: gatherer 返回资源到投放站。
        /// EntityId=gatherer, IntParam1=dropsite。原版 cmd {type:"returnresource", target}。</summary>
        public static NetCommand ReturnResource(uint player, uint gathererId, uint dropsiteId) =>
            new(player, NetCommandType.ReturnResource, gathererId, (int)dropsiteId);

        /// <summary>AttackWalk: 攻击移动到坐标。EntityId=单位, FixedParam1/2=x/z。
        /// 原版 cmd {type:"attack-walk", x, z, targetClasses}。</summary>
        public static NetCommand AttackWalk(uint player, uint unitId, Fixed x, Fixed z) =>
            new(player, NetCommandType.AttackWalk, unitId, 0, 0, x.InternalValue, z.InternalValue);

        /// <summary>WalkToRange: 移动到目标的攻击范围内。
        /// EntityId=单位, IntParam1=target, FixedParam1=minRange, FixedParam2=maxRange。</summary>
        public static NetCommand WalkToRange(uint player, uint unitId, uint targetId, Fixed minRange, Fixed maxRange) =>
            new(player, NetCommandType.WalkToRange, unitId, (int)targetId, 0, minRange.InternalValue, maxRange.InternalValue);

        /// <summary>SetupTradeRoute: 建立贸易路线。EntityId=trader, IntParam1=target market。
        /// 原版 cmd {type:"setup-trade-route", target}。</summary>
        public static NetCommand SetupTradeRoute(uint player, uint traderId, uint marketId) =>
            new(player, NetCommandType.SetupTradeRoute, traderId, (int)marketId);

        /// <summary>CollectTreasure: 收集宝藏。EntityId=collector, IntParam1=treasure。</summary>
        public static NetCommand CollectTreasureCmd(uint player, uint collectorId, uint treasureId) =>
            new(player, NetCommandType.CollectTreasure, collectorId, (int)treasureId);

        /// <summary>Guard: 护卫目标。EntityId=guard, IntParam1=target。</summary>
        public static NetCommand Guard(uint player, uint guardId, uint targetId) =>
            new(player, NetCommandType.Guard, guardId, (int)targetId);

        /// <summary>Patrol: 巡逻到坐标(起点=下单时位置,自动往返)。
        /// EntityId=单位, FixedParam1/2=x/z。原版 cmd {type:"patrol", x, z}。</summary>
        public static NetCommand Patrol(uint player, uint unitId, Fixed x, Fixed z) =>
            new(player, NetCommandType.Patrol, unitId, 0, 0, x.InternalValue, z.InternalValue);

        /// <summary>Formation: 编队命令。shape=null → 解散;否则创建编队。
        /// TemplateName = "shape|id1,id2,..."。原版 cmd {type:"formation", entities, name}。</summary>
        public static NetCommand FormationCmd(uint player, string shape, IReadOnlyList<uint> memberIds) =>
            new(player, NetCommandType.Formation, 0, 0, 0, 0, 0,
                shape + "|" + string.Join(',', memberIds));

        /// <summary>Pack: 攻城器打包(unpack=false)/解包(true)。EntityId=单位。</summary>
        /// <summary>AttackRequest: IntParam1 = 目标玩家(敌)。</summary>
        public static NetCommand AttackRequest(uint player, int targetPlayer) =>
            new(player, NetCommandType.AttackRequest, 0, targetPlayer);

        public static NetCommand Pack(uint player, uint unitId, bool unpack) =>
            new(player, NetCommandType.Pack, unitId, unpack ? 1 : 0);

        /// <summary>Upgrade: 建筑升级。EntityId=建筑, IntParam1=建造者(0=无需指派)。
        /// 原版 cmd {type:"upgrade", entities}。</summary>
        public static NetCommand Upgrade(uint player, uint buildingId, uint builderId) =>
            new(player, NetCommandType.Upgrade, buildingId, (int)builderId);

        /// <summary>Gate: 城门锁切换。EntityId=城门;locked=true 上锁(阻挡),false 解锁(通行)。</summary>
        public static NetCommand Gate(uint player, uint gateId, bool locked) =>
            new(player, NetCommandType.Gate, gateId, locked ? 1 : 0);
    }
}

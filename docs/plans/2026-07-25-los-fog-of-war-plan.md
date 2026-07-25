# LOS / 战争迷雾 实施计划(8 个 TDD 任务)

日期:2026-07-25 · 设计:`2026-07-25-los-fog-of-war-design.md` · 分支:`feat/los-fog-of-war`
执行方式:会话内顺序执行,每任务 RED→GREEN→提交。基线:247 测试。

## 关键实现约束(先读)

1. **tile-space 防溢出**:Fixed 是 Q15.16(上限 32768)。米制下 dy² 会溢出(768m 图:192²>32767 的边缘和)——strip 圆所有坐标/半径**先除 4 转 tile-space**(Fixed>>2 精确),平方比较只在 tile-space 做(25 格射程 → 平方和 ≤ 2×26² ≈ 1352,安全)。顶点索引 i 与实体的差值也受射程约束,不会溢出。
2. **确定性**:脏实体集按 `EntityId.Value` 排序;多玩家按 id 升序;事件同步分发;Fogging 回调不用事件总线,由 RangeManager 直接调用(顺序固定)。
3. **OnInit 陷阱**:组件字段在 `AddComponent` 之后再设(参考 modifiers-pipeline 经验)。
4. **系统实体模式**:LOS 全局状态挂系统实体组件(照 TerrainComponent),自动进序列化/hash。
5. **测试基线**:xUnit 串行(AssemblyInfo 已设),FindRepoPath walk-up 定位 binaries。

---

## Task 1: LosGrid(网格+计数+strip 圆)

**文件**:`src/ZeroAD.Sim/Components/LosGrid.cs`(新)、`src/ZeroAD.Sim.Tests/LosGridTests.cs`(新)

**API**:
```csharp
public enum LosState : byte { Unexplored = 0, Explored = 1, Visible = 2 }
public sealed class LosGrid
{
    public const int TileSize = 4;              // LOS_TILE_SIZE(对齐原版)
    public const int MaxPlayers = 16;           // u32 2-bit × 16
    public int VerticesPerSide { get; }         // worldMeters/4 + 1

    public LosGrid(int worldMeters);            // 分配 u32[verts²];计数 lazy
    public bool IsVisible(int player, int i, int j);
    public bool IsExplored(int player, int i, int j);
    public int  GetPercentExplored(int player); // explored*100/inworld
    public void AddLos(int player, Maths.Fixed x, Maths.Fixed z, Maths.Fixed range);    // 内部转 tile-space
    public void RemoveLos(int player, Maths.Fixed x, Maths.Fixed z, Maths.Fixed range);
    public void MoveLos(int player, Fixed fx, Fixed fz, Fixed tx, Fixed tz, Fixed range) // MVP=Remove+Add
    public void RebuildCountsClear();            // 反序列化用:计数清零(保留状态网格)
    // 序列化:state u32[](RawBytes)+ vertsPerSide + explored[p](排序玩家)
    public void Serialize(ISerializer s);
    public void Deserialize(IDeserializer d);
    public (int i, int j) WorldToVertex(Fixed x, Fixed z); // round-to-nearest, clamp
}
```

**RED 测试**(8):
1. `Packing_PerPlayerTwoBits_Independent` — p1 置可见不影响 p2
2. `AddLos_FirstSeer_SetsVisibleAndExplored`
3. `RemoveLos_LastSeer_ClearsVisible_KeepsExplored`
4. `TwoSeers_RemoveOne_StillVisible`(计数语义)
5. `StripCircle_MatchesBruteForce` — 半径 3.7/8/12.5/25 格随机中心,逐顶点对照 `dx²+dy²≤r²`(tile-space)
6. `Explored_NeverDecays`(add→remove→仍 explored)
7. `PercentExplored_Increases`
8. `Serialize_RoundTrip_PreservesExploredAndVisible`

**实现要点**:strip 逐行填充,j 从 `[y-r, y+r]`(tile-space Fixed,行顶点 = j 四舍五入);每行以上行 i0/i1 为初值,外扩内缩直到满足圆方程;`dy2 > r2` 的行跳过。计数 u16 0→1/1→0 边界写状态位 + explored 计数增量。

**验证**:`dotnet test --filter LosGridTests` 8 绿 → 提交 `feat(sim): LosGrid 视野网格(2-bit 状态+u16 计数+strip 圆填充)`

---

## Task 2: RangeManager 集成 + SetBounds + VisionComponent 复活

**文件**:`src/ZeroAD.Sim/Components/RangeManager.cs`(改)、`src/ZeroAD.Sim/Components/ExtraComponents.cs`(Vision 改 Fixed)、`src/ZeroAD.Sim/EntityAssembler.cs`(装配 Vision)、`src/ZeroAD.Sim.Tests/RangeManagerLosTests.cs`(新)

**改动**:
- `RangeEntityData` += `Fixed VisionRange; uint Visibilities; byte Flags`(bit0 InWorld 已隐式,bit1 RetainInFog,bit2 IsMirage)
- 构造签名改 `RangeManager(cm)`;`SetBounds(Fixed worldMeters)` 建/重建 LosGrid + 空间索引(修 256m 硬编码;默认 256m 保持旧行为)
- 事件接线(订阅已存在):PositionChanged → `Los.MoveLos(owner, old, new, visionRange)`(range>0 且 owner>0);OwnerChanged → 旧主 Remove/新主 Add;Created/Destroyed → 插入移除;Destroy → 若 counts>0 则 RemoveLos
- `VisionComponent`:`Fixed Range`;`EffectiveRange`(本期=Range,Task 6 接管正值);EntityAssembler:`stats.VisionRange>0` 时装配并设值(AddComponent 后赋值!)
- RetainInFog:EntityAssembler 从 `stats.RetainInFog`(Task 5 解析模板;本期先默认 false 留字段)

**RED 测试**(7):spawn 有视野实体 → 顶点可见;移动 → 旧位置失可见/新位置可见;销毁 → 失可见;换主(1→2)→ p1 失 p2 得;range=0 实体不产生视野;SetBounds 256→768 后大坐标正常工作;无视野实体移动零成本(脏集为空)。

**验证**:`dotnet test --filter RangeManagerLosTests` + 基线 → 提交 `feat(sim): RangeManager 接入 LOS(事件驱动计数更新+SetBounds 重建+Vision 定点复活)`

---

## Task 3: 每回合可见性 + VisibilityChanged + 查询 API

**文件**:`RangeManager.cs`(改)、`src/ZeroAD.Sim/Events/`(VisibilityChangedEvent)、`src/ZeroAD.Sim.Tests/LosVisibilityTests.cs`(新)

**API**:
```csharp
public enum LosVisibility : byte { Hidden = 0, Fogged = 1, Visible = 2 } // 对齐原版枚举值
// RangeManager:
public LosVisibility GetLosVisibility(EntityId ent, int player);        // 读 2-bit 缓存
public LosVisibility GetLosVisibilityPosition(Fixed x, Fixed z, int player);
public void SetLosRevealAll(int player, bool on);                        // 调试/观战
public void RequestVisibilityUpdate(EntityId ent);
public void UpdateVisibilityData();                                      // 每回合调用(SimBridge)
public event? —— 走 SimEventBus:VisibilityChangedEvent { int Player; EntityId Entity; LosVisibility Old, New }
```

`ComputeLosVisibility(ent, player)` 按设计 §2.2 八链实现(IsMirage 分支本期先读 flag,Task 5 才会置位)。`UpdateVisibilityData`:脏实体集(视野变化触及的实体由 MarkDirty 加入——MVP 简化:每回合对"拥有者可见性输入有变化"的实体;直接实现为:LosAdd/Remove 任何 strip 变化时把 owner 的该实体加入脏集 + RequestVisibilityUpdate 队列)→ 排序遍历 → 变化写缓存 + RaiseEvent + 直调 `FoggingComponent.OnVisibilityChanged`(若挂)。

**RED 测试**(8):不可见→Hidden;可见→Visible;已探索不可见+无 RetainInFog→Hidden;+RetainInFog→Fogged;reveal-all→Visible;事件恰好一次且 old/new 正确;GetLosVisibilityPosition 三态;脏集排序确定性(两实体同回合变化,事件顺序按 EntityId)。

**验证** → 提交 `feat(sim): 每回合可见性判定(HIDDEN/FOGGED/VISIBLE)+VisibilityChanged 事件`

---

## Task 4: 序列化 + 反序列化重建 + 确定性

**文件**:`src/ZeroAD.Sim/Components/LosManagerComponent.cs`(新,系统实体)、`SimBridge.cs`(挂系统实体+每回合调 UpdateVisibilityData)、`src/ZeroAD.Sim.Tests/LosSerializationTests.cs`(新)

**改动**:
- `LosManagerComponent`:持有 LosGrid + reveal-all 位;`Serialize`:vertsPerSide + state u32[](RawBytes)+ explored[排序玩家]+ revealAll 位;`Deserialize`:读回后 `RebuildCountsClear()` → 由 RangeManager 遍历全部视野实体 LosAdd 重建计数 + 重置全体脏集(下回合全量重判可见性)
- SimBridge:InitWorld 建组件挂 `_terrainEntity` 同款系统实体;`TickSimulation` 在 TickVictory 旁调 `_sim.Range.UpdateVisibilityData()`;PMP 加载后 `FillPassabilityFromPmp` 处调 `Range.SetBounds(worldM)`

**RED 测试**(5):序列化往返 state 一致;重建后计数=重建前(验证:serialize→deserialize→每顶点 counts 与源相等);explored 保留;1000 回合确定性 hash(两实例同 seed 带移动视野单位,hash 相等);基线 DeterminismTests/NetLockstepTests 全绿。

**验证** → 提交 `feat(sim): LOS 状态序列化+反序列化计数重建+确定性 hash 覆盖`

---

## Task 5: Fogging + Mirage 内核

**文件**:`src/ZeroAD.Sim/Components/Fogging.cs`(新:FoggingComponent+MirageComponent)、`TemplateLoader.cs`(+HasFogging/RetainInFog 解析)、`TemplateStats`、`EntityAssembler.cs`(+SpawnMirage)、`RangeManager.cs`(ComputeLosVisibility mirage 链补全+IsMirage 设置)、`src/ZeroAD.Sim.Tests/FoggingMirageTests.cs`(新)

**模板解析**:`<Fogging/>` 存在 → `stats.HasFogging=true`;`<Vision><RetainInFog>` → `stats.RetainInFog`(默认单位 false/建筑 true 按数据)。EntityAssembler:HasFogging → 挂 FoggingComponent。

**FoggingComponent**:
```csharp
public sealed class FoggingComponent : ComponentBase {
    public bool Activated;                        // owner>0 时激活
    public ulong SeenMask, MiragedMask;           // 每玩家 1-bit(MVP ≤16 玩家, ulong 够)
    public EntityId?[] MirageOf = new EntityId?[17]; // player → mirage
    public void OnOwnershipChanged(int newOwner);  // >0 → Activated=true
    public void OnVisibilityChanged(int player, LosVisibility vis, ComponentManager cm);
    // VISIBLE: seen 置位、miraged 清位;FOGGED 且 Activated 且 seen: LoadMirage(player)
    // LoadMirage:无现存 → cm 起 SpawnMirage + RequestVisibilityUpdate
    // Serialize:Activated + seen/miraged mask + mirage ids(排序玩家)
}
```

**MirageComponent**:`Parent EntityId; int Player; 冻结数据(FrozenHealthMax/Current、ResourceType/Amount?)`;OnVisibilityChanged(player==Player, HIDDEN)→ 真身有效则 RaiseMirageSwapBack(事件给表现层),parent 死则 `cm.DestroyEntity(self)`。

**SpawnMirage(parent, player)**:新实体 + Identity(拷)+ Ownership(拷 owner)+ Position(拷)+ MirageComponent + RangeManager 标记 IsMirage(视野 0)。**互斥**(ComputeLosVisibility):IsMirage 且顶点可见 → Hidden;真身在"已被 mirage 替代"时 → Hidden(Fogging.MiragedMask 查)。

**RED 测试**(8):建筑出雾→生成 mirage(位置/owner 一致);真身对该玩家→Hidden;回可见→真身 Visible+mirage Hidden/销毁;未见过的建筑不出 mirage(seen 语义);parent 销毁→mirage 自毁;互斥(同顶点 mirage 与真身不同时 Visible);单位(无 Fogging)出雾→纯 Hidden;序列化往返(seen/miraged/mirage 引用)。

**验证** → 提交 `feat(sim): Fogging/Mirage 雾中替身(生成/互斥/销毁全生命周期)`

---

## Task 6: 修正值 Vision/Range 接线

**文件**:`VisionComponent`(+EffectiveRange 过管线)、`RangeManager.cs`(VisionRangeChanged 处理)、`src/ZeroAD.Sim.Tests/VisionModifierTests.cs`(新)

**改动**:VisionComponent 每回合(或修正值变化时)`newRange = cm.Modifiers.Apply("Vision/Range", Range, Entity)`;变化 → RangeManager.LosRemove(old)+LosAdd(new)。MVP:SimBridge 每回合对带 Vision 的实体检查(数量少,成本可忽略);不做缓存失效事件。

**RED 测试**(3):科技 `"Vision/Range" multiply 1.5` → 顶点计数覆盖扩大;无修正 → 不变;修正消失(移除科技)→ 收缩。

**验证** → 提交 `feat(sim): Vision/Range 接入修正值管线`

---

## Task 7: 表现层显隐 + 小地图雾

**文件**:`godot/Scripts/SimBridge.cs`(+SyncVisibility)、`godot/Scripts/Minimap.cs`(雾层+LocalPlayerId 修复)、`godot/Scripts/FogTextureBuilder.cs`(新:R8 图+二项式模糊,供小地图与世界雾共用)

**改动**:
- SimBridge:每回合 sim tick 后 `SyncVisibility()`:遍历 EntityNodes,`GetLosVisibility(ent, LocalPlayerId)` → HIDDEN=`Visible=false`;FOGGED=`Visible=true`+子 MeshInstance 变暗(`modulate` 0.45 灰);VISIBLE=恢复
- Minimap:修 `playerId==1` 硬编码 → `_sim.LocalPlayerId`;加 `_fogImage`(R8,verts² → 缩到 200²),每回合 `FogTextureBuilder.Build(querier, player)` 刷新叠加(黑=未探索透明 0.85,灰=已探索透明 0.45,可见全透)
- FogTextureBuilder:纯 C#(内核无关、Godot Image 输出),7-tap (1,6,15,20,15,6,1)/64 横纵分离模糊

**RED/冒烟**:headless 跑教程 600 帧无异常 + 雾图非全 255/非全 0;GUI 手测。

**验证**:`ZEROAD_TUTORIAL=1 ... --headless --quit-after 600` EXIT=0 → 提交 `feat(godot): 实体 LOS 显隐+小地图迷雾层`

---

## Task 8: 世界雾 shader + Mirage 视觉

**文件**:`godot/Scripts/TerrainRenderer.cs`(材质换 ShaderMaterial)、`godot/Shaders/fog_terrain.gdshader`(新)、`godot/Scripts/FogWorldRenderer.cs`(新:贴图持有+每回合更新)、SimBridge(mirage 视觉)

**改动**:
- gdshader:vertex 传世界 XZ;fragment 采样 fog_tex(hidden→albedo×0.0+黑,explored→×0.45,visible→原色),`render_mode` 正常光照;贴图 UV = worldXZ/worldMeters
- FogWorldRenderer:持 ImageTexture(Task 7 builder 输出),每回合更新 `Update(fogImage)`,赋给 terrain mat + minimap
- Mirage 视觉:mirage 实体走现有 CreateVisualFor + 灰调 modulation + 不参与选中;真身回可见时 mirage 节点 QueueFree(RemoveDeadEntities 路径已覆盖实体销毁)
- 教程 1864 实体帧率验收(肉眼 + `--quit-after 3600` 无异常)

**验证**:headless 600 帧 + 窗口化截图人工确认 → 提交 `feat(godot): 世界空间战争迷雾贴图+Mirage 视觉`

---

## 验收清单(全部任务完成后)

- [ ] 内核测试:247 基线 + 新增 ~47 全绿
- [ ] 1000 回合确定性 hash 一致
- [ ] 教程 headless 600/3600 帧 EXIT=0
- [ ] 窗口化人工:迷雾随单位探索散开、建筑出雾留 mirage、回 scout mirage 消、小地图雾正确
- [ ] Godot 编译 0 警告
- [ ] 合并 main + 更新记忆(port-status 划掉迷雾)

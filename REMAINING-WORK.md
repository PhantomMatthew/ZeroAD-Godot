# 未完成项清单(2026-09-04 快照)

> 来源:[PORTING-GAPS.md](PORTING-GAPS.md) 全表核对。每条注明状态与上游对照。
> 完成项不在此列;架构性"判定不搬"见末节。

## 1. 模拟组件尾巴(§3B,对照 `simulation/components/*.js`)

| 组件 | 缺口 |
|---|---|
| ~~UnitAI~~ ✅ | Pickup 接送(乘客发起+运输侧 PICKUP 双子态+取消即完成握手)、编队控制组 obstruction 切换、炮塔站姿(standground 强切+还原)——本波全落 |
| ~~UnitMotion~~ ✅ | ~~异步路径~~ ~~朝向物理~~ ~~waypoints 序列化~~(v16 定点骑缝,读档续走+哈希连续)——全落 |
| ~~UnitSeparation~~ ✅ | push-pressure 全量(编队豁免/交叉 nudge/per-template Weight/压力累积减速/CheckMovement 钳)+ 20m 空间分格——本波全落 |
| ~~Formation~~ ✅ | LoadFormation 换模板(executor 单编队换形分支)+ IsRearrangementAllowed(>5% 关键态禁排)——本波全落 |
| ~~Garrison/Turret/Gate~~ ✅ | initGarrison/initTurrets(场景 XML 解析+生成末统一应用)、门自动开关(盟友感应+门洞占用重试+阻挡旗态机)——本波全落 |
| ~~TerritoryManager~~ ✅ | 本波校准:成本加权洪泛(8m 瓦+costGrid)+8 向连通+blink 纯驱动+百分比 |
| ~~PathfinderComponent~~ ✅ | 增量阻挡更新:两格模型+脏区打点+脏 chunk 分层局部重连(本波) |
| ~~Barter~~ ✅ | per-player BarterMultiplier 已接修正值管线(PlayerComponent Buy/Sell 表+RecomputeBarterMultipliers,BarterSystem 报价带乘数,v17 序列化) |

## 2. 内核基础设施(§4)

| 项 | 状态 | 缺口 |
|---|---|---|
| 寻路异步任务化 | ✅ | ticket+索引槽位+次回合收割(确定性);后台单任务(多 worker 需 per-worker LongPathfinder 实例——30MB scratch 驻留,按需再扩) |
| 寻路增量更新 | ✅ | 阻挡变化按脏矩形补丁+脏 chunk 局部重连(上游 UpdateGrid/HierUpdate 移植) |
| push-out / 圆形障碍 | ✅ | 本波全落:CheckLineMovement 逐字移植(逃逸链 push-out——被困单位可走出不可行格;边缘光栅化禁对角跳格)+ 图外缘印戳(此前缺失——单位可走出地图;方带 12 navcell/圆形 dist2 判式,全类 edgeMask 先于 clearance 膨胀)+ PassabilityCircular 旗(rmgen/scenario 双路径接线) |
| 序列化类型覆盖 | ✅ | U64/I64/Float/Double 全链(ISerializer+Binary+TextDump);backref 共享对象与原版二进制互通如需再对齐(当前自研格式 v17) |
| TurnManager 节奏/超时 | ✅ | 超时警示(NETWORK_WARNING_TIMEOUT=2000ms 口径:lastReceived 追踪+0.5s 巡检+PauseMenu 名单)+ host 手动踢出(ENet DisconnectPeer→掉线 AI 接管);观战者不作回合闸门(=上游 observermaxlag −1 默认) |
| Templates | ✅ | `actor\|...` 合成模板装载(ConstructTemplateActor 移植);template_not_found 上游已移除,不适用 |

## 3. Petra AI(§5)

- ~~人口规划~~✅(trainMoreWorkers 全量:在训/在队计数、popPhase2 一阶门、saveResources、饱和闸、support/soldier 指数饱和、自适应批量);~~BuildDefenses 塔楼全量门控~~✅(要塞/哨塔/石塔相位+数量+间隔+地基闸+QueueToReset 优先级回收);~~StartingStrategy 低木 saveResources 联动~~✅(开局食物/木材评估全量:needFarm/needFish/needCorral/maxFields/popPhase2×0.75/setRushes 收窄+停战门;handleNewBase 复位)。
- ~~圣物治疗者编排细节~~✅(manageCriticalEntHealers 训练 support_healer_b——saveResources/在队/神庙/护卫池四门;Create 事件接应新训治疗者=原版 TrainingFinished 等价,上游 queues.healer 唯一生产者即 victoryManager;regicide 遇袭撤退链:Attacked 事件 → ≤70% 驻防 BuffHeal 建筑/<40% 危急强驻满员腾位,驻不进 → passive 逃最近同陆基地;Garrison 上船放护卫/驻军点护卫跟随;UnGarrison 运输到位重派;EntityRenamed/Destroy 全表清账;assignGuardToCriticalEnt 异陆 requireTransport + guardedEnt 簿记;manageCriticalEntGuards 补 outOfPlan 趟;init 开局登记奇迹/英雄/圣物;存档 v21 危急旗)、~~海图换面 attackPlansEncounteredWater~~✅(建计划失败置旗对齐上游 attackPlan.failed hack 语义——AttackPlan.FailedNoTarget 派生旗,选不到/够不到目标即置;消费端 NavalManager 此前已接)。
- (bombingAttacks/外交请求-应答/圣物夺取编排已于 142df83 落地;事件面新增 sim 总线 Garrisoned/UnGarrisoned + AIEventBuffer Attacked/Garrison/UnGarrison/EntityRenamed 四类。)

## 4. 渲染 / 音频 / GUI(§7–8)

- **渲染**:~~天气(上游本无天气系统=静态环境+地图粒子 actor;粒子 actor 装配本波落地)~~;~~蒙皮阴影动画姿势(共轭骨架代理,本波落地)~~;~~粒子触发点~~✅(摧毁烟尘三件套 smoke/dust/dust_gray 按障碍半径 small/med/large 分档=原版 destruction_* 变体的粒子 props,仅被杀建筑触发——地基完工销毁不炸;建造扬尘 construction_dust 常驻地基、NumBuilders>0 才喷=原版 fndn_* actor prop;落水命中 water_splash 替代扬尘——增补触发点,上游溅花只挂瀑布 actor,记录在案;OneShotParticlePool 池化复用)。
- **音频**:~~环境音多轨叠加(现单层)~~✅(AmbientMixer 分层:基础 dayscape + 水域邻近层(相机焦点地形 vs 水位)+ 建筑邻近层(port/farm/trade,桥 GetAmbientBuildingLevels 45m 衰减)+ 天气层(图名启发:雪地/沙漠);各层独立循环播放器,增益平滑淡入淡出(0.5s 节拍驱动)。beyond-upstream——上游 Ambient.js 单轨+TODO,building/weather 数据目录存在但未接线,记录在案)。
- **GUI**:间谍请求(需逐对 LOS 共享基建);mod.io minisigs Ed25519 验签(现只验存在性);~~campaigns 末关 endgame 页/useGameSetup 分支~~✅(本波收尾:胜负均走 endgame 流程——胜 markLevelComplete、胜负均收地图脚本自定义结算数据入 run.data;ICampaignGameEndData 钩子=原版 Trigger.prototype.OnCampaignGameEnd;离开回跳战役菜单=原版 nextPage,getMenuPath)。
- **GuiInterface 桥**:~~覆盖面约原版 1/5,HUD/Minimap 热路径仍有零散 QueryInterface 直读~~本波扩面 8 查询(单选详情 SelectionDetails 一趟聚合 9 组件/选择圈 MarkerState 含 footprint+射程圈+占领段/悬停动作能力 ActionCaps 一趟/在研科技 GetStartedResearch=原版同名/站姿/编队组存活/集结点队列/相机跟随位),HUD FillSingleDetails、Main 选择圈/光标/集结点/hover 条、RTSCamera 跟随(消除 SimSystem.Sim 全局直读)全收编;余:阵型行/生产队列条/多选血微条/FindIdleUnits(模板耦合重或低频,按需再补)。

## 5. 模板 hotloading(已闭环)

- ~~存量实体 sim 字段重灌~~✅(TemplateStatsRefresher:Identity/Health/Attack/UnitMotion/Vision/Obstruction+子形状/Garrison/BuildingAI/ProductionQueue/Cost/Population/TerritoryInfluence 逐组件重灌,超上游 15 年 TODO)。hotload 全链:失效→重校验→视觉重组装+存量重灌,仍仅 debug+单机。

## 6. 架构性保留项(判定不搬 / beyond-upstream)

- 触发器任意 JS 表达力——数据驱动模型是刻意的架构选择。
- 教程 JSON 化——上游目标表也在地图 JS 里,C# 目标表是等价物。
- 真断线重连(状态转移+回合追赶)——0 A.D. 0.29 亦无此能力。
- MotionBall/Settlement(演示/空壳件)、PopulationCapManager(职能已折叠进 PlayerComponent)、Upgrade 组件(命令层等价已存在,如需原版进度条 UI 再补)。

## 7. 独立发行包(2026-09-05 开工)

目标:Godot export 自包含包,不依赖仓库外 junction;产物入 gitignore(`godot/export/`),经 CI artifact / 发布渠道分发。

- ✅ **数据根收编**:新增 `godot/Scripts/RuntimePaths.cs` 统一解析器(候选序:`ZEROAD_DATA_DIR` env → exe 旁 data/ → .app Resources → 开发期 ../binaries junction),26 文件 30+ 处 `../binaries` 探测全收编;内核本就收 dataRoot 参数,未动。
- ✅ **res:// 读取 PCK 化**:新增 `godot/Scripts/AssetIO.cs`(FileAccess/DirAccess 优先 + GlobalizePath 回退),ModelLibrary 网格/动画(GltfDocument 改 AppendFromBuffer)、TerrainRenderer/ActorComposer/InstanceCustomizer/SplatBaker/HUD/Minimap/UITheme 贴图、AssetPathResolver 目录扫描、Localization .po 全收编。
- ✅ **godot_mcp 剔除**:project.godot autoload ×3 + editor_plugins 移除(该 addon gitignored,导出环境缺类必炸);代码无引用,编辑器里重新启用插件会自动还原。
- ✅ **随包数据管线**:`godot/tools/stage_release_data.sh`(rsync 上游 data 子集 → `export/data/`,含 simulation/maps/audio/art 直读子集/gui/l10n/campaigns + mods/mod 回落层 + config),实测产出 1.4G。
- ✅ **export_presets.cfg**:三平台 exclude_filter 排除 tools/*.py/缓存杂物;`export/` 已 gitignore。
- ✅ **导出冒烟(2026-09-05 全链打通)**:macOS `--export-release` 产出 4.6G zip;解包 + ad-hoc 签名 + sandbox-exec 断 junction 实测:模板/PMP/splat/actor 全从包内 `Contents/Resources/data/` 读取(0 处 junction 引用),单位行走/采集/驻军 + Petra AI 全活。排障清单(全部已在配置/代码里固化):mono 模板装 `4.7.2.stable.mono/`、project.godot 开 `textures/vram_compression/import_etc2_astc`、bundle id 键名 `application/bundle_identifier` 且段首禁数字、export/ 加 `.gdignore` 防编辑器扫描、csproj `Compile Remove="export/**"`(上游 GLSL 计算着色器扩展名是 .cs,撞 C# 编译)、必须存在 `GodotProject.sln`(否则 C# 导出插件跳过 publish,包无程序集即 segfault)、Export 配置排除 `addons/**`(编辑器 API 仅 TOOLS 可用)、显式 `ImplicitUsings=enable`(ExportRelease 不生成 GlobalUsings)。
- ⏳ **非干净退出**:SIGTERM 杀进程时 teardown 段 `recursive_mutex lock failed` abort(游玩期无影响,排入低优先)。
- ⏳ **Linux/Windows 导出未实测**(preset 骨架在,模板已装,同管线理论可复用)。
- ⏳ **体积决策**:包 4.6G(assets PCK 压缩后)+ data 1.4G 旁置;是否开 VRAM 纹理压缩再压、剔除冗余待定。
- ⏳ **许可证随包**:0 A.D. 资产 CC-BY-SA / 代码 GPL,发布包需附 LICENSE 文本。

## 建议下一波

~~P1 性能三件套 + §3B 组件尾巴批量~~(2026-09-03 全落:增量寻路/异步路径/推挤压力
+ 门自动开关/编队组切换/炮塔站姿/重排闸门/LoadFormation/initGarrison/Pickup)。
存量最大项(2026-09-03 更新,TerritoryManager/转向/天气/阴影/人口规划均已落):
~~BuildDefenses~~✅ ~~StartingStrategy saveResources 联动~~✅ ~~BuildMoreHouses
houseNeeded~~✅(计划 GoRequirement 启动门)——Petra 经济面对齐。余:~~§4 序列化覆盖
(U64/I64/Float/Double 全实现链+位级哈希,本波;backref 共享对象无消费方,
按需再加)~~、~~GuiInterface 桥扩面(Minimap 回合缓存批量快照+CC 定位+选择集
能力 DTO 一趟扫描,Minimap 直读清零、HUD 47→31 且余者为选择重建段)~~、
~~Territory 边界描线~~✅(CTerritoryBoundaryCalculator 逐字:底边起点+Moore
变体逆时针闭环追踪+processed 位+曲率自校验;blink 判别位分环;渲染侧环带
miter+LOS 段裁剪+blink 环 TIME 脉冲 shader)、~~UnitMotion waypoints 序列化~~✅(v16:定点路标骑缝,读档续走不重复寻路,
哈希连续;顺路把 float 往返消了——确定性更好)。

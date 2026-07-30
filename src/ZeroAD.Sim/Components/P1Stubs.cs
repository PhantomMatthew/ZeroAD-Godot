using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// P1 component stubs — compilable shims so UnitAI's full state machine compiles and the core
// gameplay loop (Walk/Gather/Attack/Repair) runs. Behavior is deferred (P1 / MS5), but the
// SERIALIZABLE fields are now aligned with the original JS components at
// binaries/data/mods/public/simulation/components/, so state round-trips save/load and OOS hash
// faithfully once behaviour lands. The 5 methods UnitAI calls (Garrison/StartHealing/
// SetFirstMarket/Pack/Unpack) keep their signatures; the others have no external references.
//
// Each is marked [Component] so it auto-registers; Serialize feeds the HashSerializer, so the
// fields below automatically participate in the OOS hash.

// --- Heal: BEHAVIOR LANDED in Heal.cs (MS5, Heal.js 1:1). ---

// --- Trader: BEHAVIOR LANDED in Trader.cs (MS5, Trader.js 1:1,含 MarketComponent/Market.js
// 与 globalscripts/Trade.js 公式)。 ---

// --- Pack: BEHAVIOR LANDED in Pack.cs (MS5, Pack.js 1:1). ---

// --- Garrisonable: BEHAVIOR LANDED in Garrison.cs (MS5,Garrisonable.js 1:1,
// 含 GarrisonHolderComponent/GarrisonHolder.js)。 ---

// --- Turretable + TurretHolder: BEHAVIOR LANDED in Turret.cs (MS5,Turretable.js /
// TurretHolder.js 1:1;点位偏移/AllowedClasses/Ejectable 全量序列化,位置跟拍见
// TurretableComponent.UpdatePosition)。 ---

// --- TreasureCollector: BEHAVIOR LANDED in Treasure.cs (MS5, TreasureCollector.js 1:1,
// 含 TreasureComponent/Treasure.js)。 ---

// --- Formation: BEHAVIOR LANDED in Formation.cs (MS5, Formation.js 1:1 核心子集:
// 类组合/行列布局/逆优先级分配/质心跳转/速度同步/解散;scatter、双编队合并、编队
// 光环、编队作战见 Formation.cs 头注)。UnitAI 子树 FORMATIONCONTROLLER/
// FORMEMBER 同期落地。 ---
//

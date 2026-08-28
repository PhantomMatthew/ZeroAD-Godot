using System;
using System.Collections.Generic;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim
{
    /// <summary>
    /// Assembles components onto a freshly-created entity based on template stats.
    /// This is the sim-side counterpart to the legacy <c>SimBridge.SpawnUnit</c> assembly path;
    /// it keeps assembly in the deterministic kernel so <see cref="ComponentManager.SpawnEntity"/>
    /// can run headless and be replayed. Godot visuals stay on the presentation layer
    /// (driven by <see cref="Events.EntityCreatedEvent"/>).
    /// </summary>
    public static class EntityAssembler
    {
        /// <summary>
        /// Assemble a unit entity (mobile, optionally combatant/gatherer) from template stats.
        /// Adds: Position, UnitMotion, Health, Identity, and conditionally Attack / Gatherer+Builder /
        /// Cost / TrainingRestrictions. Does NOT add Ownership — the caller applies that separately
        /// so <see cref="ComponentManager.SpawnEntity"/> can choose whether to assign an owner.
        /// </summary>
        public static void AssembleUnit(ComponentManager cm, EntityId entity,
            string templateName, TemplateStats? stats, float x, float z)
        {
            // Formation controller(special/formations/* 模板):虚拟实体,非战斗单位——
            // 无 Health/Cost/Obstruction/Vision,不占人口,不可被攻击。
            if (stats?.HasFormation == true)
            {
                AssembleFormationController(cm, entity, templateName, stats, x, z);
                return;
            }

            bool isVillager = stats?.CanGather == true && stats.AttackDamage == 0;
            bool isSoldier = stats != null && (stats.AttackDamage > 0
                || stats.GetClassList().Contains("CitizenSoldier"));

            cm.AddComponent(entity, new PositionComponent());
            cm.AddComponent(entity, new UnitMotion());
            // UnitAI owns the order queue + state machine for mobile units. Added to all units
            // so SimBridge.Command* and lockstep commands route through the FSM.
            cm.AddComponent(entity, new UnitAIComponent());

            string name = stats?.Name ?? (isSoldier ? "Soldier" : isVillager ? "Villager" : "Unit");
            int maxHp = stats?.MaxHealth ?? (isSoldier ? 80 : 50);
            cm.AddComponent(entity, new HealthComponent
            {
                Current = maxHp,
                Max = maxHp,
                RegenRate = stats?.HealthRegenRate ?? 0f,
                IdleRegenRate = stats?.HealthIdleRegenRate ?? 0f,
            });

            var identity = new IdentityComponent
            {
                Name = name,
                TemplateName = templateName,
                IsUnit = true,
                Undeletable = stats?.Undeletable == true,
                Classes = stats?.GetClassList() ?? new List<string>()
            };
            if (isSoldier && !identity.HasClass("CitizenSoldier"))
                identity.Classes.Add("CitizenSoldier");
            cm.AddComponent(entity, identity);

            var motion = cm.QueryInterface<UnitMotion>(entity);
            if (motion != null && stats != null)
            {
                motion.Speed = Fixed.FromFloat(stats.WalkSpeed);
                // PassabilityClass(船="ship" → 水路寻路/水面出生)。
                motion.PassClassName = stats.PassabilityClass;
            }

            // Vision: seers get a Fixed-range VisionComponent (RangeManager counts their
            // circles in the LOS grid). Set AFTER AddComponent — OnInit resets the default.
            if (stats != null && stats.VisionRange > 0)
            {
                cm.AddComponent(entity, new VisionComponent());
                cm.QueryInterface<VisionComponent>(entity)!.Range = Fixed.FromInt(stats.VisionRange);
            }

            // Fog-of-war: <Fogging/> templates (structures, gaia) spawn mirages in the fog;
            // <Visibility><RetainInFog> keeps them standing in explored fog. Fields are set
            // AFTER AddComponent — OnInit resets them.
            if (stats != null && stats.HasFogging)
            {
                cm.AddComponent(entity, new FoggingComponent());
                cm.QueryInterface<FoggingComponent>(entity)!.TemplateName = templateName;
            }
            if (stats != null && stats.RetainInFog)
            {
                cm.AddComponent(entity, new VisibilityComponent());
                cm.QueryInterface<VisibilityComponent>(entity)!.RetainInFog = true;
            }

            if (isVillager || stats?.CanGather == true)
            {
                cm.AddComponent(entity, new ResourceGatherer());
                cm.AddComponent(entity, new BuilderComponent());
            }

            if (isSoldier || (stats != null && (stats.AttackDamage > 0
                || stats.AttackCaptureStrength > Maths.Fixed.Zero)))
            {
                var attack = new AttackComponent
                {
                    HasRangeOverlay = stats?.HasRangeOverlay ?? false,
                };
                if (stats != null && stats.AttackTypes.Count > 0)
                {
                    // 逐型装配(原版 Attack 组件的 Melee/Ranged slot;Capture 走独立字段)。
                    foreach (var t in stats.AttackTypes)
                    {
                        if (t.TypeName == "Capture") continue;   // Capture 走组件字段
                        var spec = new AttackComponent.AttackTypeSpec
                        {
                            Name = t.TypeName,
                            MaxRange = t.MaxRange > 0 ? t.MaxRange : 3f,
                            Rate = t.RepeatTimeMs > 0 ? 1000f / t.RepeatTimeMs : 1f,
                            RestrictedClasses = t.RestrictedClasses,
                            PreferredClasses = t.PreferredClasses,
                            StatusEffectName = t.StatusEffectName,
                            StatusEffectDurationMs = t.StatusEffectDurationMs,
                            StatusEffectIntervalMs = t.StatusEffectIntervalMs,
                            StatusEffectStackability = t.StatusEffectStackability,
                            StatusEffectDmgHack = t.StatusEffectDmgHack,
                            StatusEffectDmgPierce = t.StatusEffectDmgPierce,
                            StatusEffectDmgCrush = t.StatusEffectDmgCrush,
                            StatusEffectDmgFire = t.StatusEffectDmgFire,
                            SplashRange = t.SplashRange,
                            SplashFriendlyFire = t.SplashFriendlyFire,
                        };
                        if (t.SplashHack > 0) spec.SplashDamage.Amounts[Components.DamageType.Hack] = (int)t.SplashHack;
                        if (t.SplashPierce > 0) spec.SplashDamage.Amounts[Components.DamageType.Pierce] = (int)t.SplashPierce;
                        if (t.SplashCrush > 0) spec.SplashDamage.Amounts[Components.DamageType.Crush] = (int)t.SplashCrush;
                        if (t.SplashFire > 0) spec.SplashDamage.Amounts[Components.DamageType.Fire] = (int)t.SplashFire;
                        if (t.Hack > 0) spec.Damage.Amounts[Components.DamageType.Hack] = (int)t.Hack;
                        if (t.Pierce > 0) spec.Damage.Amounts[Components.DamageType.Pierce] = (int)t.Pierce;
                        if (t.Crush > 0) spec.Damage.Amounts[Components.DamageType.Crush] = (int)t.Crush;
                        if (t.Fire > 0) spec.Damage.Amounts[Components.DamageType.Fire] = (int)t.Fire;
                        if (spec.HasDamage) attack.Types.Add(spec);
                    }
                }
                else
                {
                    // 无逐型数据(默认路径/测试):单近战型兜底。
                    var spec = new AttackComponent.AttackTypeSpec
                    {
                        Name = stats?.AttackIsRanged == true ? "Ranged" : "Melee",
                        MaxRange = stats?.AttackRange ?? 3.0f,
                        Rate = stats?.AttackRate ?? 1.0f,
                    };
                    if (stats != null)
                    {
                        if (stats.AttackHack > 0) spec.Damage.Amounts[Components.DamageType.Hack] = stats.AttackHack;
                        if (stats.AttackPierce > 0) spec.Damage.Amounts[Components.DamageType.Pierce] = stats.AttackPierce;
                        if (stats.AttackCrush > 0) spec.Damage.Amounts[Components.DamageType.Crush] = stats.AttackCrush;
                        if (stats.AttackFire > 0) spec.Damage.Amounts[Components.DamageType.Fire] = stats.AttackFire;
                    }
                    else
                    {
                        spec.Damage.Amounts[Components.DamageType.Hack] = 20; // default melee damage
                    }
                    if (spec.HasDamage) attack.Types.Add(spec);
                }
                cm.AddComponent(entity, attack);
                if (stats != null)
                {
                    var atk = cm.QueryInterface<AttackComponent>(entity)!;
                    atk.CaptureStrength = stats.AttackCaptureStrength;
                    atk.CaptureRange = stats.AttackCaptureRange;
                    atk.CaptureRate = stats.AttackCaptureRate;
                    atk.CaptureRestrictedClasses = stats.AttackCaptureRestrictedClasses;
                    // 组件级偏好/限制 = 首个物理型的(兼容面;逐型在 Types 里各有一份)。
                    if (atk.Types.Count > 0)
                    {
                        atk.PreferredClasses = atk.Types[0].PreferredClasses;
                        atk.PhysicalRestrictedClasses = atk.Types[0].RestrictedClasses;
                    }
                    // ApplyStatus(攻击附带状态效果;火攻船 Burning 等)。
                    atk.StatusEffectName = stats.StatusEffectName;
                    atk.StatusEffectDurationMs = stats.StatusEffectDurationMs;
                    atk.StatusEffectIntervalMs = stats.StatusEffectIntervalMs;
                    atk.StatusEffectStackability = stats.StatusEffectStackability;
                    atk.StatusEffectDmgHack = stats.StatusEffectDamageHack;
                    atk.StatusEffectDmgPierce = stats.StatusEffectDamagePierce;
                    atk.StatusEffectDmgCrush = stats.StatusEffectDamageCrush;
                    atk.StatusEffectDmgFire = stats.StatusEffectDamageFire;
                }
            }

            // Heal(治疗者;template_unit_support_healer 系):Heal.js 行为件,UnitAI HEAL 状态驱动。
            if (stats != null && stats.HasHeal && cm.QueryInterface<HealComponent>(entity) == null)
            {
                var heal = new HealComponent
                {
                    HealAmount = stats.HealAmount,
                    Range = stats.HealRange,
                    Rate = stats.HealInterval,
                };
                cm.AddComponent(entity, heal);
                heal.HealableClasses.AddRange(Content.EntityClassHelper.ParseClassTokens(stats.HealHealableClasses));
                heal.UnhealableClasses.AddRange(Content.EntityClassHelper.ParseClassTokens(stats.HealUnhealableClasses));
            }

            // Pack(攻城器打包/展开;template_unit_siege_*):Pack.js 行为件。
            if (stats != null && stats.HasPack && cm.QueryInterface<PackComponent>(entity) == null)
            {
                cm.AddComponent(entity, new PackComponent
                {
                    PackTime = stats.PackTime,
                    Packed = stats.PackStartsPacked,
                    PackEntity = stats.PackEntity,
                });
            }

            // TreasureCollector(template_unit 默认件):TreasureCollector.js 行为件。
            if (stats != null && stats.HasTreasureCollector
                && cm.QueryInterface<TreasureCollectorComponent>(entity) == null)
            {
                cm.AddComponent(entity, new TreasureCollectorComponent
                {
                    MaxDistance = stats.TreasureCollectorMaxDistance,
                });
            }

            // Trader(贸易单位;template_unit_support_trader 系):Trader.js 行为件。
            if (stats != null && stats.HasTrader && cm.QueryInterface<TraderComponent>(entity) == null)
            {
                cm.AddComponent(entity, new TraderComponent
                {
                    GainMultiplier = stats.TraderGainMultiplier,
                    GarrisonGainMultiplier = stats.TraderGarrisonGainMultiplier,
                });
            }

            // Loot + Looter + StatusEffectsReceiver(原版 template_unit 默认件):
            // 战利品定义按模板挂载;收集器/接收器全体单位恒挂(空架/框架件)。
            if (stats != null && stats.HasLoot && cm.QueryInterface<LootComponent>(entity) == null)
            {
                cm.AddComponent(entity, new LootComponent
                {
                    Xp = stats.LootXp,
                    Food = stats.LootFood,
                    Wood = stats.LootWood,
                    Stone = stats.LootStone,
                    Metal = stats.LootMetal,
                });
            }
            if (cm.QueryInterface<LooterComponent>(entity) == null)
                cm.AddComponent(entity, new LooterComponent());
            if (cm.QueryInterface<StatusEffectsReceiverComponent>(entity) == null)
                cm.AddComponent(entity, new StatusEffectsReceiverComponent());

            // Repairable(攻城器/船;template_unit_siege/template_unit_ship):修理行为件。
            if (stats != null && stats.HasRepairable
                && cm.QueryInterface<RepairableComponent>(entity) == null)
            {
                cm.AddComponent(entity, new RepairableComponent
                {
                    RepairTimeRatio = stats.RepairTimeRatio,
                });
            }

            // ResourceTrickle(资源涓流单位——罕见;建筑走 RegisterForLos)。
            if (stats != null && stats.HasResourceTrickle
                && cm.QueryInterface<ResourceTrickleComponent>(entity) == null)
            {
                cm.AddComponent(entity, new ResourceTrickleComponent
                {
                    IntervalMs = stats.TrickleIntervalMs,
                    FoodRate = stats.TrickleFood,
                    WoodRate = stats.TrickleWood,
                    StoneRate = stats.TrickleStone,
                    MetalRate = stats.TrickleMetal,
                });
            }

            // ── P0 补齐件(§3A):DeathDamage/Upkeep/AutoBuildable/AlertRaiser/飞行标记 ──
            if (stats != null && stats.HasDeathDamage
                && cm.QueryInterface<DeathDamageComponent>(entity) == null)
            {
                cm.AddComponent(entity, new DeathDamageComponent
                {
                    Range = stats.DeathDamageRange,
                    FriendlyFire = stats.DeathDamageFriendlyFire,
                    Damage = new DamageBlock
                    {
                        Amounts =
                        {
                            [DamageType.Hack] = stats.DeathDamageHack,
                            [DamageType.Pierce] = stats.DeathDamagePierce,
                            [DamageType.Crush] = stats.DeathDamageCrush,
                            [DamageType.Fire] = stats.DeathDamageFire,
                        },
                    },
                });
            }
            if (stats != null && stats.HasUpkeep
                && cm.QueryInterface<UpkeepComponent>(entity) == null)
            {
                cm.AddComponent(entity, new UpkeepComponent
                {
                    IntervalMs = stats.UpkeepIntervalMs,
                    Food = stats.UpkeepFood,
                    Wood = stats.UpkeepWood,
                    Stone = stats.UpkeepStone,
                    Metal = stats.UpkeepMetal,
                });
            }
            if (stats != null && stats.HasAutoBuildable
                && cm.QueryInterface<AutoBuildableComponent>(entity) == null)
            {
                cm.AddComponent(entity, new AutoBuildableComponent { Rate = stats.AutoBuildRate });
            }
            if (stats != null && stats.HasAlertRaiser
                && cm.QueryInterface<AlertRaiserComponent>(entity) == null)
            {
                cm.AddComponent(entity, new AlertRaiserComponent
                {
                    List = stats.AlertRaiserList,
                    RaiseAlertRange = stats.AlertRaiseRange,
                    EndOfAlertRange = stats.AlertEndRange,
                    SearchRange = stats.AlertSearchRange,
                });
            }
            if (stats != null && stats.HasUnitMotionFlying
                && cm.QueryInterface<UnitMotion>(entity) is { } flyingMotion)
            {
                flyingMotion.IsFlying = true;
                flyingMotion.Speed = Maths.Fixed.FromFloat(stats.FlyingMaxSpeed);
            }
            // Promotion(军衔晋升链):此前从未装配 → XP 从不累计、士兵永不升段。
            if (stats != null && stats.HasPromotion
                && cm.QueryInterface<PromotionComponent>(entity) == null)
            {
                cm.AddComponent(entity, new PromotionComponent
                {
                    PromoteTo = stats.PromotionEntity,
                    XpNext = stats.PromotionRequiredXp,
                });
            }

            // Garrisonable(可驻防;template_unit 默认 Size=1):Garrisonable.js 行为件。
            if (stats != null && stats.GarrisonableSize > 0
                && cm.QueryInterface<GarrisonableComponent>(entity) == null)
            {
                cm.AddComponent(entity, new GarrisonableComponent { Size = stats.GarrisonableSize });
            }

            // GarrisonHolder(载客单位如船/攻城器;建筑走 RegisterForLos):GarrisonHolder.js 行为件。
            if (stats != null && stats.HasGarrisonHolder
                && cm.QueryInterface<GarrisonHolderComponent>(entity) == null)
            {
                var holderCmp = new GarrisonHolderComponent
                {
                    Max = stats.GarrisonCapacity,
                    BuffHeal = stats.GarrisonHolderBuffHeal,
                    LoadingRange = stats.GarrisonHolderLoadingRange,
                    EjectHealth = stats.GarrisonHolderEjectHealth,
                    Pickup = stats.GarrisonHolderPickup,
                    EjectClassesOnDestroy = stats.GarrisonHolderEjectClasses,
                };
                cm.AddComponent(entity, holderCmp);
                holderCmp.AllowedClasses.AddRange(
                    Content.EntityClassHelper.ParseClassTokens(stats.GarrisonHolderList));
            }

            // Turretable(可上炮塔点;远程兵系):Turretable.js 行为件。
            if (stats != null && stats.HasTurretable
                && cm.QueryInterface<TurretableComponent>(entity) == null)
            {
                cm.AddComponent(entity, new TurretableComponent());
            }

            // TurretHolder(载具/船侧炮塔点;城墙走 RegisterForLos):TurretHolder.js 行为件。
            if (stats != null && stats.HasTurretHolder
                && cm.QueryInterface<TurretHolderComponent>(entity) == null)
            {
                AddTurretHolder(cm, entity, stats);
            }

            // Resistance: anything with Health can resist damage. Attached unconditionally for
            // units so the DamageBlock→Resistance→Health pipeline has a component to consult.
            if (stats != null &&
                (stats.ResistanceHack != 0 || stats.ResistancePierce != 0 ||
                 stats.ResistanceCrush != 0 || stats.ResistanceCapture != 0 || stats.ResistanceFire != 0))
            {
                var res = new ResistanceComponent();
                if (stats.ResistanceHack != 0) res.Resistances[Components.DamageType.Hack] = stats.ResistanceHack;
                if (stats.ResistancePierce != 0) res.Resistances[Components.DamageType.Pierce] = stats.ResistancePierce;
                if (stats.ResistanceCrush != 0) res.Resistances[Components.DamageType.Crush] = stats.ResistanceCrush;
                if (stats.ResistanceFire != 0) res.Resistances[Components.DamageType.Fire] = stats.ResistanceFire;
                res.CaptureResistance = stats.ResistanceCapture;
                cm.AddComponent(entity, res);
            }

            // Cost: real template cost (consumed by training refund / entity-limits accounting).
            // Added unconditionally for units so the pop counter and EntityLimits can read it.
            cm.AddComponent(entity, new CostComponent
            {
                WoodCost = stats?.WoodCost ?? 0,
                FoodCost = stats?.FoodCost ?? 0,
                StoneCost = stats?.StoneCost ?? 0,
                MetalCost = stats?.MetalCost ?? 0,
                PopulationCost = stats?.PopulationCost ?? 1,
                BuildTime = stats?.BuildTime ?? 5f
            });

            // TrainingRestrictions: category tag (Civilian/Hero/WarDog/...) used by EntityLimits.
            if (stats != null && !string.IsNullOrEmpty(stats.TrainingCategory))
                cm.AddComponent(entity, new TrainingRestrictionsComponent { Category = stats.TrainingCategory });

            // Auras: <Auras datatype="tokens"> 空格分词的 aura 文件名。仅当模板声明非空 aura
            // 才挂组件(避免给每个单位挂空组件)。Configure 注入 cm 引用供 OnDeinit 清残留。
            if (stats != null && !string.IsNullOrWhiteSpace(stats.Auras))
            {
                var auraCmp = new AuraComponent();
                auraCmp.Configure(
                    stats.Auras.Split(' ', StringSplitOptions.RemoveEmptyEntries), cm);
                cm.AddComponent(entity, auraCmp);
            }

            // Obstruction: unit circle so other units route around it and it can't be walked through.
            // Template may override the radius; default to ~1m clearance. Registered with the
            // ObstructionManager on EnsureRegistered (called by SimBridge after spawn completes).
            cm.AddComponent(entity, new ObstructionComponent
            {
                Type = ObstructionType.Unit,
                Size0 = (stats != null && stats.ObstructionSize0 > Fixed.Zero)
                    ? stats.ObstructionSize0
                    : Fixed.FromFloat(1.0f),
                Flags = ObstructionFlags.BlockMovement | ObstructionFlags.BlockFoundation
            });

            var pos = cm.QueryInterface<PositionComponent>(entity);
            if (pos != null)
            {
                // 通知 RangeManager 更新 spatial subdivision(同 SimBridge.SpawnUnit 的修复):
                // 此前直接字段赋值 → subdivision 里实体在 (0,0) → ExecuteQuery 查不到 → 不攻击。
                var p = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
                pos.Position = p;
                cm.NotifyPositionChanged(entity,
                    new Maths.FixedVector2D(Maths.Fixed.Zero, Maths.Fixed.Zero),
                    new Maths.FixedVector2D(p.X, p.Z));
            }

            // Register the obstruction now that Position is set, so it's tracked from frame 1.
            cm.QueryInterface<ObstructionComponent>(entity)?.EnsureRegistered();
        }

        /// <summary>
        /// LOS registration for entities assembled outside <see cref="AssembleUnit"/> — the
        /// legacy SimBridge scenario/sandbox spawns and kernel foundations create entities
        /// directly and would otherwise never enter the RangeManager index (rendering them
        /// permanently HIDDEN) nor carry Vision/Fogging/Visibility. Attaches the fog-of-war
        /// components from template stats (idempotent), notifies the RangeManager, then fires
        /// the ownership message that activates fogging when an owner is present.
        /// Call AFTER ownership (if any) is assigned.
        /// </summary>
        public static void RegisterForLos(ComponentManager cm, EntityId entity,
            string templateName, TemplateStats? stats)
        {
            if (stats != null && stats.VisionRange > 0
                && cm.QueryInterface<VisionComponent>(entity) == null)
            {
                cm.AddComponent(entity, new VisionComponent());
                cm.QueryInterface<VisionComponent>(entity)!.Range = Fixed.FromInt(stats.VisionRange);
            }
            if (stats != null && stats.HasFogging
                && cm.QueryInterface<FoggingComponent>(entity) == null)
            {
                cm.AddComponent(entity, new FoggingComponent());
                cm.QueryInterface<FoggingComponent>(entity)!.TemplateName = templateName;
            }
            if (stats != null && stats.RetainInFog
                && cm.QueryInterface<VisibilityComponent>(entity) == null)
            {
                cm.AddComponent(entity, new VisibilityComponent());
                cm.QueryInterface<VisibilityComponent>(entity)!.RetainInFog = true;
            }

            // Auras:建筑/foundation/legacy 路径补挂(这些不走 AssembleUnit)。AssembleUnit
            // 已为 unit 挂过 → QueryInterface 幂等判定跳过,不重复。
            if (stats != null && !string.IsNullOrWhiteSpace(stats.Auras)
                && cm.QueryInterface<AuraComponent>(entity) == null)
            {
                var auraCmp = new AuraComponent();
                auraCmp.Configure(
                    stats.Auras.Split(' ', StringSplitOptions.RemoveEmptyEntries), cm);
                cm.AddComponent(entity, auraCmp);
            }

            // TerritoryInfluence:同 auras —— 建筑/foundation 路径补挂,QueryInterface 幂等。
            if (stats != null && stats.TerritoryInfluenceRadius > Maths.Fixed.Zero
                && cm.QueryInterface<TerritoryInfluenceComponent>(entity) == null)
            {
                cm.AddComponent(entity, new TerritoryInfluenceComponent
                {
                    Radius = stats.TerritoryInfluenceRadius,
                    Weight = stats.TerritoryInfluenceWeight,
                    Root = stats.TerritoryInfluenceRoot,
                });
            }

            // Treasure(gaia 宝物;template_gaia_treasure):同走建筑/gaia 装配路径补挂。
            if (stats != null && stats.HasTreasure
                && cm.QueryInterface<TreasureComponent>(entity) == null)
            {
                cm.AddComponent(entity, new TreasureComponent
                {
                    CollectTimeSec = stats.TreasureCollectTime,
                    Food = stats.TreasureFood,
                    Wood = stats.TreasureWood,
                    Stone = stats.TreasureStone,
                    Metal = stats.TreasureMetal,
                });
            }

            // Market(市场/船坞;template_structure_economic_market):Trader.js 贸易端点。
            if (stats != null && stats.HasMarket
                && cm.QueryInterface<MarketComponent>(entity) == null)
            {
                var marketCmp = new MarketComponent
                {
                    InternationalBonus = stats.MarketInternationalBonus,
                };
                cm.AddComponent(entity, marketCmp);
                marketCmp.TradeTypes.AddRange(
                    Content.EntityClassHelper.ParseClassTokens(stats.MarketTradeTypes));
            }

            // GarrisonHolder(驻军建筑;civil_centre/fortress 等):GarrisonHolder.js 行为件。
            if (stats != null && stats.HasGarrisonHolder
                && cm.QueryInterface<GarrisonHolderComponent>(entity) == null)
            {
                var holderCmp = new GarrisonHolderComponent
                {
                    Max = stats.GarrisonCapacity,
                    BuffHeal = stats.GarrisonHolderBuffHeal,
                    LoadingRange = stats.GarrisonHolderLoadingRange,
                    EjectHealth = stats.GarrisonHolderEjectHealth,
                    Pickup = stats.GarrisonHolderPickup,
                    EjectClassesOnDestroy = stats.GarrisonHolderEjectClasses,
                };
                cm.AddComponent(entity, holderCmp);
                holderCmp.AllowedClasses.AddRange(
                    Content.EntityClassHelper.ParseClassTokens(stats.GarrisonHolderList));
            }

            // TurretHolder(城墙/哨塔炮塔点):TurretHolder.js 行为件。
            if (stats != null && stats.HasTurretHolder
                && cm.QueryInterface<TurretHolderComponent>(entity) == null)
            {
                AddTurretHolder(cm, entity, stats);
            }

            // TerritoryDecay + Capturable(原版 template_structure 默认件):领土衰减闭环。
            // Capturable 首主 CP 拉满须在 Ownership 已读后(对齐原版首个 OnOwnershipChanged)。
            if (stats != null && stats.HasTerritoryDecay
                && cm.QueryInterface<TerritoryDecayComponent>(entity) == null)
            {
                cm.AddComponent(entity, new TerritoryDecayComponent
                {
                    DecayRate = stats.TerritoryDecayRate,
                    Territory = stats.TerritoryDecayTerritory,
                    TerritoryOwnership = stats.TerritoryDecayOwnership,
                });
            }
            if (stats != null && stats.HasCapturable
                && cm.QueryInterface<CapturableComponent>(entity) == null)
            {
                var capturable = new CapturableComponent
                {
                    MaxCapturePoints = stats.CapturablePoints,
                    BaseMaxCapturePoints = stats.CapturablePoints,
                    RegenRate = stats.CapturableRegenRate,
                    GarrisonRegenRate = stats.CapturableGarrisonRegenRate,
                };
                cm.AddComponent(entity, capturable);
                capturable.InitForOwner(cm.QueryInterface<OwnershipComponent>(entity)?.PlayerId ?? -1);
            }

            // Loot + Looter + StatusEffectsReceiver(原版 template_structure 默认件)。
            if (stats != null && stats.HasLoot && cm.QueryInterface<LootComponent>(entity) == null)
            {
                cm.AddComponent(entity, new LootComponent
                {
                    Xp = stats.LootXp,
                    Food = stats.LootFood,
                    Wood = stats.LootWood,
                    Stone = stats.LootStone,
                    Metal = stats.LootMetal,
                });
            }
            if (cm.QueryInterface<LooterComponent>(entity) == null)
                cm.AddComponent(entity, new LooterComponent());
            if (cm.QueryInterface<StatusEffectsReceiverComponent>(entity) == null)
                cm.AddComponent(entity, new StatusEffectsReceiverComponent());

            // Repairable(可修理建筑;template_structure 默认 RepairTimeRatio=2.0)。
            if (stats != null && stats.HasRepairable
                && cm.QueryInterface<RepairableComponent>(entity) == null)
            {
                cm.AddComponent(entity, new RepairableComponent
                {
                    RepairTimeRatio = stats.RepairTimeRatio,
                });
            }

            // ResourceTrickle(奇观/牲口棚等资源涓流建筑)。
            if (stats != null && stats.HasResourceTrickle
                && cm.QueryInterface<ResourceTrickleComponent>(entity) == null)
            {
                cm.AddComponent(entity, new ResourceTrickleComponent
                {
                    IntervalMs = stats.TrickleIntervalMs,
                    FoodRate = stats.TrickleFood,
                    WoodRate = stats.TrickleWood,
                    StoneRate = stats.TrickleStone,
                    MetalRate = stats.TrickleMetal,
                });
            }

            cm.NotifyEntityCreated(entity); // RangeManager subscribes → indexes + Refresh
            int owner = cm.QueryInterface<OwnershipComponent>(entity)?.PlayerId ?? -1;
            if (owner > 0)
                cm.NotifyOwnerChanged(entity, -1, owner); // activates fogging (MT_OwnershipChanged)
        }

        /// <summary>
        /// Assemble a formation controller (special/formations/* templates). Port of the original
        /// controller entity: Position + UnitMotion + UnitAI(FormationController) + Formation.
        /// Deliberately NO Health/Cost/Obstruction/Vision — the controller is virtual: it can't
        /// be damaged, costs no pop, blocks nothing, and provides no LOS. Ownership is applied
        /// by the caller (ComponentManager.SpawnEntity) as usual.
        /// </summary>
        private static void AssembleFormationController(ComponentManager cm, EntityId entity,
            string templateName, TemplateStats stats, float x, float z)
        {
            cm.AddComponent(entity, new PositionComponent());
            cm.AddComponent(entity, new UnitMotion());
            var ai = new UnitAIComponent();
            cm.AddComponent(entity, ai);
            ai.InitAsFormationController();
            cm.AddComponent(entity, new IdentityComponent
            {
                Name = "Formation",
                TemplateName = templateName,
                IsUnit = false,
                Undeletable = stats.Undeletable,   // template_formation: Undeletable=true
                Classes = new List<string> { "Formation" },
            });
            var formation = new FormationComponent
            {
                RequiredMemberCount = stats.FormationRequiredMemberCount,
                SpeedMultiplier = stats.FormationSpeedMultiplier,
                Shape = stats.FormationShape,
                MaxTurningAngle = stats.FormationMaxTurningAngle,
                SortingOrder = stats.FormationSortingOrder,
                ShiftRows = stats.FormationShiftRows,
                UnitSeparationWidthMultiplier = stats.FormationSepWidthMultiplier,
                UnitSeparationDepthMultiplier = stats.FormationSepDepthMultiplier,
                Sloppiness = stats.FormationSloppiness,
                WidthDepthRatio = stats.FormationWidthDepthRatio,
                MinColumns = stats.FormationMinColumns,
                MaxColumns = stats.FormationMaxColumns,
                MaxRows = stats.FormationMaxRows,
                CenterGap = stats.FormationCenterGap,
                CanAttackAsFormation = stats.FormationCanAttackAsFormation,
            };
            cm.AddComponent(entity, formation);
            formation.SortingClasses.AddRange(stats.FormationSortingClasses);

            var pos = cm.QueryInterface<PositionComponent>(entity);
            if (pos != null)
            {
                var p = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
                pos.Position = p;
                cm.NotifyPositionChanged(entity,
                    new Maths.FixedVector2D(Maths.Fixed.Zero, Maths.Fixed.Zero),
                    new Maths.FixedVector2D(p.X, p.Z));
            }
        }

        /// <summary>Attach a TurretHolder with its template-defined named points
        /// (TurretHolder.js Init). Shared by the unit and structure assembly paths.</summary>
        private static void AddTurretHolder(ComponentManager cm, EntityId entity, TemplateStats stats)
        {
            var th = new TurretHolderComponent
            {
                LoadingRange = stats.TurretHolderLoadingRange,
                Pickup = stats.TurretHolderPickup,
            };
            cm.AddComponent(entity, th);
            foreach (var def in stats.TurretPoints)
                th.TurretPoints.Add(new TurretHolderComponent.TurretPoint
                {
                    Name = def.Name,
                    OffsetX = def.X,
                    OffsetY = def.Y,
                    OffsetZ = def.Z,
                    AllowedClasses = def.AllowedClasses,
                    Angle = def.Angle,
                    Template = def.Template,
                    Ejectable = def.Ejectable,
                });
        }

        /// <summary>
        /// Spawn a mirage: the lean frozen stand-in for <paramref name="parent"/> in one
        /// player's fog (Fogging.js LoadMirage + the special/filter/mirage.xml derivation).
        /// Carries only Mirage + Position + Ownership (+ Visibility when the parent retains
        /// in fog) — no Vision (mirages don't see), no Health (can't be damaged), no
        /// Identity (doesn't count for conquest/pop). Notifies the RangeManager so the
        /// mirage enters the visibility chain with IsMirage+RetainInFog flags.
        /// </summary>
        public static EntityId SpawnMirage(ComponentManager cm, RangeManager rm,
            EntityId parent, int player, string templateName)
        {
            var mirage = cm.CreateEntity();
            cm.AddComponent(mirage, new MirageComponent());
            var mc = cm.QueryInterface<MirageComponent>(mirage)!;
            mc.Parent = parent;
            mc.Player = player;

            cm.AddComponent(mirage, new PositionComponent());
            var parentOwner = cm.QueryInterface<OwnershipComponent>(parent);
            if (parentOwner != null)
                cm.AddComponent(mirage, new OwnershipComponent { PlayerId = parentOwner.PlayerId });
            // The mirage filter keeps the parent's Visibility → retain-in-fog carries over.
            if (cm.QueryInterface<VisibilityComponent>(parent)?.RetainInFog == true)
            {
                cm.AddComponent(mirage, new VisibilityComponent());
                cm.QueryInterface<VisibilityComponent>(mirage)!.RetainInFog = true;
            }

            RefreshMirageData(cm, parent, mirage);
            cm.NotifyEntityCreated(mirage);
            rm.RefreshFromComponents(mirage); // flags IsMirage/RetainInFog, indexes position
            // Presentation builds the mirage's visual from the parent's template (the fog
            // shader/dimming distinguishes it); same spawn-event pattern as SpawnEntity.
            cm.Events.RaiseEntityCreated(new Events.EntityCreatedEvent
            {
                Entity = mirage,
                TemplateName = templateName,
                OwnerPlayerId = parentOwner?.PlayerId ?? -1
            });
            return mirage;
        }

        /// <summary>
        /// (Re)freeze a mirage's data from its parent: position + rotation (JumpTo semantics —
        /// a position notification is sent, a no-op if the mirage isn't tracked yet) and the
        /// last-seen health/resource amounts. Called on every fog cycle, so a reused mirage
        /// reflects what the player saw most recently.
        /// </summary>
        public static void RefreshMirageData(ComponentManager cm, EntityId parent, EntityId mirage)
        {
            var parentPos = cm.QueryInterface<PositionComponent>(parent);
            var miragePos = cm.QueryInterface<PositionComponent>(mirage);
            if (parentPos != null && miragePos != null)
            {
                var old = new FixedVector2D(miragePos.Position.X, miragePos.Position.Z);
                miragePos.Position = parentPos.Position;
                miragePos.Rotation = parentPos.Rotation;
                cm.NotifyPositionChanged(mirage, old,
                    new FixedVector2D(parentPos.Position.X, parentPos.Position.Z));
            }

            var mc = cm.QueryInterface<MirageComponent>(mirage);
            if (mc == null) return;
            var health = cm.QueryInterface<HealthComponent>(parent);
            if (health != null)
            {
                mc.FrozenHealthCurrent = health.Current;
                mc.FrozenHealthMax = health.Max;
            }
            mc.FrozenResourceAmount = cm.QueryInterface<ResourceSupply>(parent)?.Amount ?? -1;
        }
    }
}

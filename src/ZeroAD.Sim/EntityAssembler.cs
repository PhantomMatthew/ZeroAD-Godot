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
            cm.AddComponent(entity, new HealthComponent { Current = maxHp, Max = maxHp });

            var identity = new IdentityComponent
            {
                Name = name,
                TemplateName = templateName,
                IsUnit = true,
                Classes = stats?.GetClassList() ?? new List<string>()
            };
            if (isSoldier && !identity.HasClass("CitizenSoldier"))
                identity.Classes.Add("CitizenSoldier");
            cm.AddComponent(entity, identity);

            var motion = cm.QueryInterface<UnitMotion>(entity);
            if (motion != null && stats != null)
                motion.Speed = Fixed.FromFloat(stats.WalkSpeed);

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

            if (isSoldier || (stats != null && stats.AttackDamage > 0))
            {
                // Build the multi-type damage block from template stats.
                var dmg = new Components.DamageBlock();
                if (stats != null)
                {
                    if (stats.AttackHack > 0) dmg.Amounts[Components.DamageType.Hack] = stats.AttackHack;
                    if (stats.AttackPierce > 0) dmg.Amounts[Components.DamageType.Pierce] = stats.AttackPierce;
                    if (stats.AttackCrush > 0) dmg.Amounts[Components.DamageType.Crush] = stats.AttackCrush;
                    dmg.Capture = stats.AttackCapture;
                }
                else
                {
                    dmg.Amounts[Components.DamageType.Hack] = 20; // default melee damage
                }
                cm.AddComponent(entity, new AttackComponent
                {
                    Damage = dmg,
                    Range = stats?.AttackRange ?? 3.0f,
                    Rate = stats?.AttackRate ?? 1.0f,
                    IsRanged = stats?.AttackIsRanged ?? false
                });
            }

            // Resistance: anything with Health can resist damage. Attached unconditionally for
            // units so the DamageBlock→Resistance→Health pipeline has a component to consult.
            if (stats != null &&
                (stats.ResistanceHack != 0 || stats.ResistancePierce != 0 ||
                 stats.ResistanceCrush != 0 || stats.ResistanceCapture != 0))
            {
                var res = new ResistanceComponent();
                if (stats.ResistanceHack != 0) res.Resistances[Components.DamageType.Hack] = stats.ResistanceHack;
                if (stats.ResistancePierce != 0) res.Resistances[Components.DamageType.Pierce] = stats.ResistancePierce;
                if (stats.ResistanceCrush != 0) res.Resistances[Components.DamageType.Crush] = stats.ResistanceCrush;
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
                pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));

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

            cm.NotifyEntityCreated(entity); // RangeManager subscribes → indexes + Refresh
            int owner = cm.QueryInterface<OwnershipComponent>(entity)?.PlayerId ?? -1;
            if (owner > 0)
                cm.NotifyOwnerChanged(entity, -1, owner); // activates fogging (MT_OwnershipChanged)
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

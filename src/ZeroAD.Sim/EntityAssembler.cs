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
    }
}

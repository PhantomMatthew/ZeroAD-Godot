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

            if (isVillager || stats?.CanGather == true)
            {
                cm.AddComponent(entity, new ResourceGatherer());
                cm.AddComponent(entity, new BuilderComponent());
            }

            if (isSoldier || (stats != null && stats.AttackDamage > 0))
            {
                cm.AddComponent(entity, new AttackComponent
                {
                    Damage = stats?.AttackDamage ?? 20,
                    Range = stats?.AttackRange ?? 3.0f,
                    Rate = stats?.AttackRate ?? 1.0f
                });
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

            var pos = cm.QueryInterface<PositionComponent>(entity);
            if (pos != null)
                pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        }
    }
}

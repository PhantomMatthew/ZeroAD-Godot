using System;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components
{
    /// <summary>Placement-type controlling which terrain class the building needs. Mirrors
    /// <c>BuildRestrictions.js</c> PlacementType.</summary>
    public enum BuildPlacementType { Land, Shore, LandShore }

    /// <summary>Structured result of a placement check, returned to the GUI so it can localize the
    /// reason. Mirrors the strings BuildRestrictions.js returns.</summary>
    public sealed class PlacementCheckResult
    {
        public bool Success;
        public PlacementResult Reason;     // FailOutOfBounds / FailTerrain / FailObstructsFoundation
        public string Category = "";       // building category (CivilCentre, House, ...)
    }

    /// <summary>
    /// Per-building placement rules. Mirrors <c>BuildRestrictions.js</c>: a building can only be
    /// placed where terrain + obstructions allow, within optional min/max distance of another
    /// building class (e.g. a Civil Centre must be near a Settlement), and inside an allowed
    /// territory type (own/ally/neutral/enemy; unconnected own/ally needs "neutral" too).
    /// The LOS check from the original is skipped ( fog-of-war placement masking isn't ported).
    /// </summary>
    [Component("BuildRestrictions", "BuildRestrictions")]
    public sealed class BuildRestrictionsComponent : ComponentBase, IComponentMessageHandler
    {
        public BuildPlacementType PlacementType = BuildPlacementType.Land;
        public string Category = "";
        /// <summary>BuildRestrictions/Territory tokens(空格分隔 own/ally/neutral/enemy)。
        /// 空串 = 无领土限制。装配自模板(template_structure 默认 "own")。</summary>
        public string Territory = "";
        // Distance constraint: must be within [MinDistance, MaxDistance] of some building whose
        // Identity matches FromClass. Empty FromClass = no distance constraint.
        public string FromClass = "";
        public Fixed MinDistance;
        public Fixed MaxDistance;

        protected override void OnInit() { }

        /// <summary>
        /// Validate placing this building at its current PositionComponent location. Checks:
        /// terrain (via Pathfinder), obstructions (via ObstructionComponent.CheckFoundation),
        /// optional distance-to-class (via RangeManager). Returns a structured result so the
        /// caller (Main.PlaceBuilding) can report why it failed.
        /// </summary>
        public PlacementCheckResult CheckPlacement()
        {
            var result = new PlacementCheckResult { Category = Category };
            var pos = SimSystem.GetComponent<PositionComponent>(Entity);
            if (pos == null)
            {
                result.Reason = PlacementResult.FailOutOfBounds;
                return result;
            }

            Fixed x = pos.Position.X, z = pos.Position.Z;

            // Terrain + obstruction check via ObstructionComponent → Pathfinder.
            var obs = SimSystem.GetComponent<ObstructionComponent>(Entity);
            if (obs != null)
            {
                FoundationCheck fc = obs.CheckFoundation(passClass: "building-land");
                if (fc == FoundationCheck.FailNoObstruction)
                {
                    result.Reason = PlacementResult.FailOutOfBounds;
                    return result;
                }
                if (fc == FoundationCheck.FailObstructsFoundation)
                {
                    result.Reason = PlacementResult.FailObstructsFoundation;
                    return result;
                }
            }
            else
            {
                // No ObstructionComponent: fall back to a direct Pathfinder check on the footprint.
                var pf = SimSystem.Pathfinder;
                if (pf != null)
                {
                    Fixed hw = Fixed.FromInt(2), hh = Fixed.FromInt(2);
                    var pr = pf.CheckBuildingPlacement(x, z, hw, hh);
                    if (pr != PlacementResult.Success)
                    {
                        result.Reason = pr;
                        return result;
                    }
                }
            }

            // Territory check(对齐 BuildRestrictions.js:186-240):own/ally/neutral/enemy,
            // 未连通 own/ally 还需 "neutral"。玩家归属取本实体 OwnershipComponent。
            var territory = SimSystem.Territory;
            var owner = SimSystem.GetComponent<OwnershipComponent>(Entity);
            if (territory != null && owner != null && owner.PlayerId > 0
                && !territory.CanBuildHere(Territory, owner.PlayerId, x, z))
            {
                result.Reason = PlacementResult.FailTerritory;
                return result;
            }

            // Optional distance-to-class constraint (e.g. "must be within 60m of a CivilCentre").
            if (!string.IsNullOrEmpty(FromClass) && MaxDistance > Fixed.Zero)
            {
                var range = SimSystem.Range;
                if (range != null)
                {
                    var cm = SimSystem.Sim;
                    bool found = false;
                    // Search out to MaxDistance for any building matching FromClass.
                    var nearby = range.ExecuteQuery(Entity, MinDistance, MaxDistance, eid =>
                    {
                        var id = cm?.QueryInterface<IdentityComponent>(eid);
                        return id != null && id.HasClass(FromClass);
                    });
                    found = nearby.Count > 0;
                    if (!found)
                    {
                        result.Reason = PlacementResult.FailOutOfBounds; // no qualifying building in range
                        return result;
                    }
                }
            }

            result.Success = true;
            return result;
        }

        public override void Serialize(ISerializer s)
        {
            s.NumberI32("placement", (int)PlacementType);
            s.StringASCII("cat", Category);
            s.StringASCII("terr", Territory);
            s.StringASCII("from", FromClass);
            s.NumberFixed("min", MinDistance);
            s.NumberFixed("max", MaxDistance);
        }

        public override void Deserialize(IDeserializer d)
        {
            PlacementType = (BuildPlacementType)d.NumberI32("placement");
            Category = d.StringASCII("cat");
            Territory = d.StringASCII("terr");
            FromClass = d.StringASCII("from");
            MinDistance = d.NumberFixed("min");
            MaxDistance = d.NumberFixed("max");
        }

        public void HandleMessage(IMessage message) { }
    }
}

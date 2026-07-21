using System;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components
{
    /// <summary>Footprint shape: circle or square. Matches <c>CCmpFootprint::EShape</c>.</summary>
    public enum FootprintShape { Circle, Square }

    /// <summary>
    /// Describes an entity's physical footprint on the ground — the area it occupies and that
    /// newly-trained units must avoid when spawning. Ported from <c>CCmpFootprint</c>.
    ///
    /// <see cref="PickSpawnPoint"/> finds a free position just outside this entity's footprint for a
    /// unit of a given radius to appear at, used by ProductionQueue when training completes. The
    /// search walks rings around the footprint edge and returns the first cell the Pathfinder
    /// accepts; if none works it returns a sentinel (caller falls back to a fixed offset).
    /// </summary>
    [Component("Footprint", "Footprint")]
    public sealed class FootprintComponent : ComponentBase, IComponentMessageHandler
    {
        public FootprintShape Shape = FootprintShape.Square;
        // Square: Size0=width, Size1=depth. Circle: Size0=radius (Size1 = Size0).
        public Fixed Size0 = Fixed.FromInt(4);
        public Fixed Size1 = Fixed.FromInt(4);
        public Fixed Height = Fixed.Zero;
        // How far from the footprint to search for a spawn point (default ~8m, matching original).
        public Fixed MaxSpawnDistance = Fixed.FromInt(8);

        protected override void OnInit() { }

        /// <summary>
        /// Find a world position outside this footprint where a unit of radius
        /// <paramref name="spawnedRadius"/> can stand without overlapping obstructions or
        /// impassable terrain. Returns (x, 0, z) on success or (-1, -1, -1) if no slot found.
        ///
        /// Simplified port of <c>CCmpFootprint::PickSpawnPoint</c>: ring search around the footprint
        /// edge using the Pathfinder to validate each candidate. The original's rally-point-biased
        /// ordering and square-perimeter coordinate walk are flattened to a simpler radial sweep —
        /// good enough for the common "train a villager outside the TC" case.
        /// </summary>
        public FixedVector3D PickSpawnPoint(Fixed spawnedRadius)
        {
            var pos = SimSystem.GetComponent<PositionComponent>(Entity);
            var pf = SimSystem.Pathfinder;
            if (pos == null || pf == null)
                return new FixedVector3D(Fixed.FromInt(-1), Fixed.FromInt(-1), Fixed.FromInt(-1));

            Fixed cx = pos.Position.X, cz = pos.Position.Z;
            // Footprint half-extent along X/Z (square: half width/depth; circle: radius).
            Fixed halfX = Shape == FootprintShape.Circle ? Size0 : Size0 / Fixed.FromInt(2);
            Fixed halfZ = Shape == FootprintShape.Circle ? Size0 : Size1 / Fixed.FromInt(2);

            // Skip this entity's own obstruction when sampling — the spawn ring sits just outside
            // the footprint but close enough that the trainer's own StaticShape would otherwise
            // collide with every candidate, blocking all spawns.
            var selfObs = SimSystem.GetComponent<ObstructionComponent>(Entity);
            ObstructionTag? skipTag = (selfObs != null && selfObs.Tag.IsValid) ? selfObs.Tag : null;

            // Walk outward in rings; each ring's radius grows by `gap`. For each ring, sample N
            // points evenly around it. Return the first the Pathfinder accepts.
            Fixed gap = spawnedRadius.Multiply(Fixed.FromInt(3)) + Fixed.FromInt(1);
            int maxRings = Math.Max(1, (MaxSpawnDistance / gap).ToIntRoundToInfinity());

            for (int ring = 0; ring < maxRings; ring++)
            {
                // Distance from center to the ring's sample circle.
                Fixed ringDist = (Shape == FootprintShape.Circle ? Size0
                    : Fixed.Zero.WithInternalValue(
                        (int)MathInt.Sqrt64((ulong)((long)halfX.InternalValue * halfX.InternalValue)
                            + (ulong)((long)halfZ.InternalValue * halfZ.InternalValue))))
                    + gap.Multiply(Fixed.FromInt(ring + 1));
                int numPoints = 8 + ring * 4; // more points on outer rings
                for (int i = 0; i < numPoints; i++)
                {
                    // Spawn-time (not per-tick) computation; position is deterministic given i/numPoints.
                    float a = (float)(2 * Math.PI * i / numPoints);
                    Fixed sx = cx + Fixed.FromFloat((float)Math.Cos(a)).Multiply(ringDist);
                    Fixed sz = cz + Fixed.FromFloat((float)Math.Sin(a)).Multiply(ringDist);

                    var pr = pf.CheckUnitPlacement(sx, sz, spawnedRadius, skipTag);
                    if (pr == PlacementResult.Success)
                        return new FixedVector3D(sx, Fixed.Zero, sz);
                }
            }

            return new FixedVector3D(Fixed.FromInt(-1), Fixed.FromInt(-1), Fixed.FromInt(-1));
        }

        public override void Serialize(ISerializer s)
        {
            s.NumberI32("shape", (int)Shape);
            s.NumberFixed("s0", Size0);
            s.NumberFixed("s1", Size1);
            s.NumberFixed("h", Height);
            s.NumberFixed("maxd", MaxSpawnDistance);
        }

        public override void Deserialize(IDeserializer d)
        {
            Shape = (FootprintShape)d.NumberI32("shape");
            Size0 = d.NumberFixed("s0");
            Size1 = d.NumberFixed("s1");
            Height = d.NumberFixed("h");
            MaxSpawnDistance = d.NumberFixed("maxd");
        }

        public void HandleMessage(IMessage message) { }
    }
}

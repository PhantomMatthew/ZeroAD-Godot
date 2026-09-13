using System;

namespace ZeroAD.Sim.RL;

/// <summary>Frozen AlphaStar-style observation / action dimensions for the P0 C# environment.</summary>
public static class RlSpec
{
    public const int MaxEntities = 512;
    public const int EntityFeat = 13;
    public const int SpatialSize = 64;
    public const int SpatialChannels = 4;
    public const int ScalarCount = 11;
    public const int MaxSelected = 1;
    public const int WorldMetersDefault = 256;
    public const uint LayoutMagic = 0x5A414452; // "ZADR"
    public const int LayoutVersion = 1;

    public static class Ent
    {
        public const int Valid = 0;
        public const int OwnerRel = 1;
        public const int TemplateId = 2;
        public const int CellX = 3;
        public const int CellZ = 4;
        public const int Hp = 5;
        public const int HpMax = 6;
        public const int UnitAi = 7;
        public const int Flags = 8;
        public const int Visibility = 9;
        public const int EntityId = 10;
        public const int PosXInternal = 11;
        public const int PosZInternal = 12;

        public const int FlagBuilding = 1;
        public const int FlagCanAttack = 2;
        public const int FlagCanGather = 4;
        public const int FlagFoundation = 8;
    }

    public static class OwnerRel
    {
        public const int None = 0;
        public const int Self = 1;
        public const int Ally = 2;
        public const int Enemy = 3;
        public const int Neutral = 4;
    }

    public static class Spat
    {
        public const int Visibility = 0;
        public const int Explored = 1;
        public const int Passable = 2;
        public const int Territory = 3;
    }

    public static class Scal
    {
        public const int Wood = 0;
        public const int Food = 1;
        public const int Stone = 2;
        public const int Metal = 3;
        public const int Pop = 4;
        public const int PopCap = 5;
        public const int Turn = 6;
        public const int Phase = 7;
        public const int Won = 8;
        public const int Defeated = 9;
        public const int Privileged = 10;
    }
}

public sealed class RlConfig
{
    public uint Seed { get; init; } = 1;
    public int AgentPlayerId { get; init; } = 1;
    public int OpponentPlayerId { get; init; } = 2;
    public bool PrivilegedVision { get; init; }
    public int StepMul { get; init; } = 1;
    public int CommandDelay { get; init; } = 2;
    public int MaxEpisodeTurns { get; init; } = 10_000;
    public int WorldMeters { get; init; } = RlSpec.WorldMetersDefault;
    public bool TickOpponentAi { get; init; }
}

public sealed class RlObservation
{
    public readonly int[] Entities;
    public readonly int[] Spatial;
    public readonly int[] Scalars;
    public readonly byte[] FunctionMask;
    public uint Turn;
    public bool Done;
    public int Reward;

    public RlObservation()
    {
        Entities = new int[RlSpec.MaxEntities * RlSpec.EntityFeat];
        Spatial = new int[RlSpec.SpatialChannels * RlSpec.SpatialSize * RlSpec.SpatialSize];
        Scalars = new int[RlSpec.ScalarCount];
        FunctionMask = new byte[Enum.GetValues<RlFunction>().Length];
    }

    public int Entity(int row, int feat) => Entities[row * RlSpec.EntityFeat + feat];
    public void SetEntity(int row, int feat, int value) => Entities[row * RlSpec.EntityFeat + feat] = value;
}

public enum RlFunction : byte
{
    NoOp = 0,
    Stop = 1,
    Move = 2,
    Attack = 3,
    Gather = 4,
    Repair = 5,
    Build = 6,
    Train = 7,
    Research = 8,
    Garrison = 9,
}

public readonly struct RlAction
{
    public readonly RlFunction Function;
    public readonly int SelectedIndex;
    public readonly int TargetEntityIndex;
    public readonly int TargetCellX;
    public readonly int TargetCellZ;
    public readonly int CatalogId;

    public RlAction(RlFunction function, int selectedIndex = -1, int targetEntityIndex = -1,
        int targetCellX = 0, int targetCellZ = 0, int catalogId = 0)
    {
        Function = function;
        SelectedIndex = selectedIndex;
        TargetEntityIndex = targetEntityIndex;
        TargetCellX = targetCellX;
        TargetCellZ = targetCellZ;
        CatalogId = catalogId;
    }

    public static RlAction NoOp() => new(RlFunction.NoOp);
}

public sealed class RlStepResult
{
    public RlObservation Observation { get; init; } = null!;
    public int Reward { get; init; }
    public bool Done { get; init; }
}

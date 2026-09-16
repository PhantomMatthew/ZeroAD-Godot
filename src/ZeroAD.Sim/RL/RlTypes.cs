namespace ZeroAD.Sim.RL;

/// <summary>AlphaStar-style observation / action dimensions. LayoutVersion 4 adds
/// per-entity carry / production-queue / research fields (feats 13–19).</summary>
public static class RlSpec
{
    public const int MaxEntities = 512;
    public const int EntityFeat = 20;
    public const int SpatialSize = 64;
    public const int SpatialChannels = 5;
    public const int ScalarCount = 11;
    public const int MaxSelected = 8;
    /// <summary>Must equal the <see cref="RlFunction"/> enum length and stay ≤ 32
    /// (packed into a uint32 per-entity mask).</summary>
    public const int FunctionCount = 32;
    /// <summary>Packed intern ids per entity for Train / Build / Research.</summary>
    public const int CatalogKindCount = 3;
    public const int MaxCatalogChoices = 16;
    public const int CatalogLength = MaxEntities * CatalogKindCount * MaxCatalogChoices;
    public const int WorldMetersDefault = 256;
    public const uint LayoutMagic = 0x5A414452; // "ZADR"
    public const int LayoutVersion = 4;

    public static class Cat
    {
        public const int Train = 0;
        public const int Build = 1;
        public const int Research = 2;
    }

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
        /// <summary>0 = empty hands; else 1+<see cref="ZeroAD.Sim.Components.ResourceType"/>.</summary>
        public const int CarryType = 13;
        public const int CarryAmount = 14;
        public const int QueueCount = 15;
        public const int QueueId = 16;
        public const int QueueProgress = 17;
        public const int ResearchId = 18;
        public const int ResearchProgress = 19;

        public const int FlagBuilding = 1;
        public const int FlagCanAttack = 2;
        public const int FlagCanGather = 4;
        public const int FlagFoundation = 8;
        /// <summary>Set when a RallyPoint queue has at least one position.</summary>
        public const int FlagRallySet = 16;
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
        public const int Resource = 4;
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
    /// <summary>Empty = 1v1 encounter sandbox. rmgen registry name (e.g. <c>mainland</c>)
    /// or a scenario-relative path under mods/public (e.g. <c>maps/skirmishes/Acropolis Bay</c>).</summary>
    public string MapName { get; init; } = "";
    /// <summary>rmgen tile count (typically 128–192). Ignored for PMP / encounter.</summary>
    public int MapSize { get; init; } = 128;
    /// <summary>When true, pick agent/opponent civs from <see cref="Seed"/> instead of
    /// the athen/athen default.</summary>
    public bool RandomizeCivs { get; init; }
    /// <summary>Staged mods/public (or a parent that contains it). Null = walk-up from the process.</summary>
    public string? DataRoot { get; init; }
    /// <summary>When templates are found, spawn a 1v1 encounter (CC + soldiers + villagers + trees)
    /// instead of the dummy seer pair. Disable to force the sandbox even if data is present.</summary>
    public bool UseRealMatch { get; init; } = true;
    public string AgentCiv { get; init; } = "athen";
    public string OpponentCiv { get; init; } = "athen";
    public int SoldiersPerSide { get; init; } = 3;
    public int VillagersPerSide { get; init; } = 2;
    public int PetraDifficulty { get; init; }
}

public sealed class RlObservation
{
    public readonly int[] Entities;
    public readonly int[] Spatial;
    public readonly int[] Scalars;
    public readonly byte[] FunctionMask;
    /// <summary>Bit i of row r means <see cref="RlFunction"/> i is legal for that entity.</summary>
    public readonly uint[] EntityMask;
    /// <summary>Per-entity intern ids: [row][kind][choice], kinds Train/Build/Research.</summary>
    public readonly int[] Catalog;
    public uint Turn;
    public bool Done;
    public int Reward;

    public RlObservation()
    {
        Entities = new int[RlSpec.MaxEntities * RlSpec.EntityFeat];
        Spatial = new int[RlSpec.SpatialChannels * RlSpec.SpatialSize * RlSpec.SpatialSize];
        Scalars = new int[RlSpec.ScalarCount];
        FunctionMask = new byte[RlSpec.FunctionCount];
        EntityMask = new uint[RlSpec.MaxEntities];
        Catalog = new int[RlSpec.CatalogLength];
    }

    public int Entity(int row, int feat) => Entities[row * RlSpec.EntityFeat + feat];
    public void SetEntity(int row, int feat, int value) => Entities[row * RlSpec.EntityFeat + feat] = value;

    public int CatalogAt(int row, int kind, int slot) =>
        Catalog[CatalogIndex(row, kind, slot)];

    public void SetCatalog(int row, int kind, int slot, int internId) =>
        Catalog[CatalogIndex(row, kind, slot)] = internId;

    public static int CatalogIndex(int row, int kind, int slot) =>
        ((row * RlSpec.CatalogKindCount) + kind) * RlSpec.MaxCatalogChoices + slot;
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
    Patrol = 10,
    AttackWalk = 11,
    ReturnResource = 12,
    Ungarrison = 13,
    Rally = 14,
    Guard = 15,
    Delete = 16,
    Stance = 17,
    CancelProduction = 18,
    Pack = 19,
    Upgrade = 20,
    Gate = 21,
    CollectTreasure = 22,
    WalkToRange = 23,
    FocusFire = 24,
    SpyRequest = 25,
    Formation = 26,
    Tribute = 27,
    Barter = 28,
    SetupTradeRoute = 29,
    AttackRequest = 30,
    SetTradingGoods = 31,
}

public readonly struct RlAction
{
    public readonly RlFunction Function;
    public readonly int SelectedIndex;
    public readonly int TargetEntityIndex;
    public readonly int TargetCellX;
    public readonly int TargetCellZ;
    public readonly int CatalogId;
    public readonly int Selected1;
    public readonly int Selected2;
    public readonly int Selected3;
    public readonly int Selected4;
    public readonly int Selected5;
    public readonly int Selected6;
    public readonly int Selected7;

    public RlAction(RlFunction function, int selectedIndex = -1, int targetEntityIndex = -1,
        int targetCellX = 0, int targetCellZ = 0, int catalogId = 0,
        int selected1 = -1, int selected2 = -1, int selected3 = -1,
        int selected4 = -1, int selected5 = -1, int selected6 = -1, int selected7 = -1)
    {
        Function = function;
        SelectedIndex = selectedIndex;
        TargetEntityIndex = targetEntityIndex;
        TargetCellX = targetCellX;
        TargetCellZ = targetCellZ;
        CatalogId = catalogId;
        Selected1 = selected1;
        Selected2 = selected2;
        Selected3 = selected3;
        Selected4 = selected4;
        Selected5 = selected5;
        Selected6 = selected6;
        Selected7 = selected7;
    }

    public int SelectedAt(int i) => i switch
    {
        0 => SelectedIndex,
        1 => Selected1,
        2 => Selected2,
        3 => Selected3,
        4 => Selected4,
        5 => Selected5,
        6 => Selected6,
        7 => Selected7,
        _ => -1
    };

    public RlAction WithSelected(int selectedIndex) =>
        new(Function, selectedIndex, TargetEntityIndex, TargetCellX, TargetCellZ, CatalogId);

    public static RlAction NoOp() => new(RlFunction.NoOp);
}

public sealed class RlStepResult
{
    public RlObservation Observation { get; init; } = null!;
    public int Reward { get; init; }
    public bool Done { get; init; }
}

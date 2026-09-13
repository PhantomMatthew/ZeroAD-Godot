using System;
using System.Collections.Generic;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>AI 难度级别（原版 petra/difficultyLevel.js）。</summary>
public static class DifficultyLevel
{
    public const int Sandbox = 0;
    public const int VeryEasy = 1;
    public const int Easy = 2;
    public const int Medium = 3;
    public const int Hard = 4;
    public const int VeryHard = 5;
}

/// <summary>Petra 配置（原版 petra/config.js，357 行）。难度/性格参数 + setConfig 逻辑 + Cheat。
/// 所有数值逐字移植；setConfig 的参数调整逻辑保持一致。</summary>
public sealed class PetraConfig
{
    public int Difficulty;
    public string Behavior;

    public bool Chat = true;
    public double PopScaling = 1;

    public readonly MilitaryConfig Military = new();
    public readonly Dictionary<string, double> DamageTypeImportance = new()
    {
        ["Hack"] = 0.075, ["Pierce"] = 0.085, ["Crush"] = 0.045, ["Fire"] = 0.001,
    };
    public readonly EconomyConfig Economy = new();
    public readonly DefenseConfig Defense = new();

    // per-civ 额外建筑（phase 3 时建造;原版 config.js buildings 全量逐字,
    // 键以原版实际为准——pers 等无表文明回落 default 空表）
    public readonly Dictionary<string, List<string>> Buildings = new()
    {
        ["default"] = new(),
        ["achae"] = new() { "structures/{civ}/tachara" },
        ["athen"] = new() { "structures/{civ}/gymnasium", "structures/{civ}/prytaneion", "structures/{civ}/theater" },
        ["brit"] = new(),
        ["cart"] = new() { "structures/{civ}/embassy_celtic", "structures/{civ}/embassy_iberian", "structures/{civ}/embassy_italic" },
        ["gaul"] = new() { "structures/{civ}/assembly" },
        ["han"] = new() { "structures/{civ}/academy" },
        ["iber"] = new() { "structures/{civ}/monument" },
        ["kush"] = new() { "structures/{civ}/camp_blemmye", "structures/{civ}/camp_noba", "structures/{civ}/pyramid_large", "structures/{civ}/pyramid_small", "structures/{civ}/temple_amun" },
        ["mace"] = new() { "structures/{civ}/theater" },
        ["maur"] = new() { "structures/{civ}/palace", "structures/{civ}/pillar_ashoka" },
        ["ptol"] = new() { "structures/{civ}/library", "structures/{civ}/theater" },
        ["rome"] = new() { "structures/{civ}/army_camp", "structures/{civ}/temple_vesta" },
        ["sele"] = new() { "structures/{civ}/theater" },
        ["spart"] = new() { "structures/{civ}/syssiton", "structures/{civ}/theater" },
    };

    public readonly Dictionary<string, int> Priorities = new()
    {
        ["villager"] = 300, ["citizenSoldier"] = 600, ["trader"] = 1, ["healer"] = 20,
        ["ships"] = 1, ["house"] = 250, ["dropsites"] = 950, ["field"] = 480, ["dock"] = 90,
        ["corral"] = 1, ["economicBuilding"] = 700, ["militaryBuilding"] = 330,
        ["defenseBuilding"] = 70, ["civilCentre"] = 1, ["majorTech"] = 700,
        ["minorTech"] = 250, ["wonder"] = 1, ["emergency"] = 1000,
    };

    public PersonalityData Personality = new();
    public PersonalityCutData PersonalityCut = new() { Weak = 0.3, Medium = 0.5, Strong = 0.7 };

    public readonly GarrisonHealthLevelData GarrisonHealthLevel = new();

    /// <summary>队列优先级覆盖(原版 config.queues:按时间窗的资源阈值)。
    /// QueueManager 按当前时间窗查资源余量时用。</summary>
    public readonly Dictionary<string, Dictionary<string, int>> Queues = new()
    {
        ["firstTurn"] = new() { ["food"] = 10, ["wood"] = 10, ["default"] = 0 },
        ["short"] = new() { ["food"] = 200, ["wood"] = 200, ["default"] = 100 },
        ["medium"] = new() { ["default"] = 0 },
        ["long"] = new() { ["default"] = 0 },
    };

    /// <summary>无盟友时不研的科技(原版 unusedNoAllyTechs:共享类在无盟友局浪费)。</summary>
    public readonly List<string> UnusedNoAllyTechs = new()
    {
        "Player/sharedLos", "Market/InternationalBonus", "Player/sharedDropsites",
    };

    public readonly List<double> CriticalPopulationFactors = new() { 0.8, 0.8, 0.7, 0.6, 0.5, 0.35 };
    public readonly List<double> CriticalStructureFactors = new() { 0.8, 0.8, 0.7, 0.6, 0.5, 0.35 };
    public readonly List<double> CriticalRootFactors = new() { 0.8, 0.8, 0.67, 0.5, 0.35, 0.2 };

    public EmergencyValuesData? EmergencyValues;

    public PetraConfig(int difficulty = DifficultyLevel.Medium, string behavior = "random")
    { Difficulty = difficulty; Behavior = behavior; }

    /// <summary>根据 gameState（人口上限/胜利条件）和难度/性格调整全部参数。
    /// 逐字移植 setConfig（config.js:204-325）。</summary>
    public void SetConfig(CommonApi.GameState gameState, Rand48 rng)
    {
        if (Difficulty > DifficultyLevel.Sandbox)
        {
            var personalityList = new Dictionary<string, (double min, double max)>
            {
                ["random"] = (0, 1),
                ["defensive"] = (0, 0.27),
                ["balanced"] = (0.37, 0.63),
                ["aggressive"] = (0.73, 1),
            };
            double behavior = rng.NextDouble() * 1 - 0.5;  // randFloat(-0.5, 0.5)
            double variation = 0.15 * (rng.NextDouble() * 2 - 1) * Math.Sqrt(0.25 - behavior * behavior);
            double aggressive = Math.Clamp(behavior + variation, -0.5, 0.5) + 0.5;
            double defensive = Math.Clamp(-behavior + variation, -0.5, 0.5) + 0.5;
            var (min, max) = personalityList.GetValueOrDefault(Behavior, (0, 1));
            Personality = new PersonalityData
            {
                Aggressive = min + aggressive * (max - min),
                Defensive = 1 - max + defensive * (max - min),
                Cooperative = rng.NextDouble(),
            };
        }

        // 难度/性格调整
        Military.TowerLapseTime = (int)Math.Round(Military.TowerLapseTime * (1.1 - 0.2 * Personality.Defensive));
        Military.FortressLapseTime = (int)Math.Round(Military.FortressLapseTime * (1.1 - 0.2 * Personality.Defensive));
        Priorities["defenseBuilding"] = (int)Math.Round(Priorities["defenseBuilding"] * (0.9 + 0.2 * Personality.Defensive));

        if (Difficulty < DifficultyLevel.Easy)
        {
            PopScaling = 0.5;
            Economy.SupportRatio = 0.5;
            Economy.ProvisionFields = 1;
            Military.NumSentryTowers = Personality.Defensive > PersonalityCut.Strong ? 1 : 0;
        }
        else if (Difficulty < DifficultyLevel.Medium)
        {
            PopScaling = 0.7;
            Economy.SupportRatio = 0.4;
            Economy.ProvisionFields = 1;
            Military.NumSentryTowers = Personality.Defensive > PersonalityCut.Strong ? 1 : 0;
        }
        else
        {
            Military.NumSentryTowers = Difficulty == DifficultyLevel.Medium ? 1 : 2;
            if (Personality.Defensive > PersonalityCut.Strong) Military.NumSentryTowers++;
            else if (Personality.Defensive < PersonalityCut.Weak) Military.NumSentryTowers--;

            if (Personality.Aggressive > PersonalityCut.Strong)
            {
                Military.PopForBarracks1 = 12;
                Economy.PopPhase2 = 50;
                Priorities["healer"] = 10;
            }
        }

        int maxPop = gameState.GetPopulationMax();
        if (Difficulty < DifficultyLevel.Easy)
            Economy.TargetNumWorkers = Math.Max(1, Math.Min(40, maxPop));
        else if (Difficulty < DifficultyLevel.Medium)
            Economy.TargetNumWorkers = Math.Max(1, Math.Min(60, maxPop / 2));
        else
            Economy.TargetNumWorkers = Math.Max(1, Math.Min(120, maxPop / 3));
        Economy.TargetNumTraders = 2 + Difficulty;

        if (maxPop < 300)
            PopScaling *= Math.Sqrt((double)maxPop / 300);

        Military.PopForBarracks1 = Math.Min(Math.Max((int)(Military.PopForBarracks1 * PopScaling), 12), maxPop / 5);
        Military.PopForBarracks2 = Math.Min(Math.Max((int)(Military.PopForBarracks2 * PopScaling), 45), maxPop * 2 / 3);
        Military.PopForForge = Math.Min(Math.Max((int)(Military.PopForForge * PopScaling), 30), maxPop / 2);
        Economy.PopPhase2 = Math.Min(Math.Max((int)(Economy.PopPhase2 * PopScaling), 20), maxPop / 2);
        Economy.WorkPhase3 = Math.Min(Math.Max((int)(Economy.WorkPhase3 * PopScaling), 40), maxPop * 2 / 3);
        Economy.WorkPhase4 = Math.Min(Math.Max((int)(Economy.WorkPhase4 * PopScaling), 45), maxPop * 2 / 3);
        Economy.TargetNumTraders = (int)Math.Round(Economy.TargetNumTraders * PopScaling);
        Economy.TargetNumWorkers = Math.Max(Economy.TargetNumWorkers, Economy.PopPhase2);
        Economy.WorkPhase3 = Math.Min(Economy.WorkPhase3, Economy.TargetNumWorkers);
        Economy.WorkPhase4 = Math.Min(Economy.WorkPhase4, Economy.TargetNumWorkers);
        if (Difficulty < DifficultyLevel.Easy)
            Economy.WorkPhase3 = int.MaxValue;  // prevent phasing to city

        EmergencyValues = new EmergencyValuesData
        {
            Population = CriticalPopulationFactors[Math.Min(Difficulty, CriticalPopulationFactors.Count - 1)],
            Structures = CriticalStructureFactors[Math.Min(Difficulty, CriticalStructureFactors.Count - 1)],
            Roots = CriticalRootFactors[Math.Min(Difficulty, CriticalRootFactors.Count - 1)],
        };

        Cheat(gameState);
    }

    /// <summary>原版 config.js Cheat(325-338)逐字移植:按难度给 AI 玩家挂作弊修正——
    /// 采集速率与贸易收益 × rate[difficulty],建造/训练/研究时间 × time[difficulty]
    /// (难度 0-5 = Sandbox…VeryHard;越界钳到表尾)。
    /// 经 ModifiersManager 落到 AI 玩家实体(原版 AddModifiers("AI Bonus", …, playerData.entity)
    /// 同款;affects = Unit+Structure)。AddModifiers 同 modId+路径+目标拒重 → 幂等。
    /// 调用链:SetConfig 末段(config.js:318);另由 AIComponent.DeserializeHq 在读档后
    /// 重挂(ModifiersManager 是派生态不序列化,读档须重放,与科技重放同理由)。</summary>
    public void Cheat(CommonApi.GameState gameState)
    {
        // Sandbox, Very Easy, Easy, Medium, Hard, Very Hard
        // rate 作用于资源囤积(采集与贸易);time 作用于建造/升级/打包/训练/科技
        double[] rate = { 0.42, 0.56, 0.75, 1.00, 1.25, 1.56 };
        double[] time = { 1.40, 1.25, 1.10, 1.00, 1.00, 1.00 };
        int aiDiff = Math.Min(Difficulty, rate.Length - 1);
        var playerEntity = gameState.Cm.GetPlayerEntityId(gameState.PlayerId);
        if (playerEntity == null) return;
        var affects = new List<string> { "Unit", "Structure" };
        gameState.Cm.Modifiers.AddModifiers("AI Bonus", new List<Components.Modification>
        {
            new("ResourceGatherer/BaseSpeed", null, (float)rate[aiDiff], null, affects),
            new("Trader/GainMultiplier", null, (float)rate[aiDiff], null, affects),
            new("Cost/BuildTime", null, (float)time[aiDiff], null, affects),
        }, playerEntity.Value);
    }

    // ── 嵌套配置类型 ──

    public sealed class MilitaryConfig
    {
        public int TowerLapseTime = 360;
        public int FortressLapseTime = 390;
        public int PopForBarracks1 = 25;
        public int PopForBarracks2 = 55;
        public int PopForForge = 65;
        public int NumSentryTowers = 1;
    }

    public sealed class EconomyConfig
    {
        public int PopPhase2 = 150;
        public int WorkPhase3 = 180;
        public int WorkPhase4 = 200;
        public int PopForDock = 25;
        public int TargetNumWorkers = 60;
        public int TargetNumTraders = 1;
        public int TargetNumFishers = 1;
        public double SupportRatio = 0.3;
        public int ProvisionFields = 2;
    }

    public sealed class DefenseConfig
    {
        public (double ally, double neutral, double own) DefenseRatio = (1.4, 1.8, 2.0);
        public int ArmyCompactSize = 2000;
        public int ArmyBreakawaySize = 3500;
        public int ArmyMergeSize = 1400;
    }

    public sealed class PersonalityData { public double Aggressive = 0.5; public double Cooperative = 0.5; public double Defensive = 0.5; }
    public sealed class PersonalityCutData { public double Weak; public double Medium; public double Strong; }
    public sealed class GarrisonHealthLevelData { public double Low = 0.4; public double Medium = 0.55; public double High = 0.7; }
    public sealed class EmergencyValuesData { public double Population; public double Structures; public double Roots; }
}

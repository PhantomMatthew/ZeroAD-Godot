using System;
using System.Collections.Generic;
using ZeroAD.Sim.Triggers;

namespace ZeroAD.Sim.Content
{
    public sealed class CampaignScenario
    {
        public string Name { get; set; } = "Scenario";
        public string Description { get; set; } = "";
        public List<ScenarioEntity> Entities { get; set; } = new();
        public List<ScenarioTrigger> Triggers { get; set; } = new();

        public static CampaignScenario CreateTutorial()
        {
            return new CampaignScenario
            {
                Name = "First Steps",
                Description = "Learn the basics: select, move, gather, build.",
                Entities = new()
                {
                    new() { Template = "units/athen/support_female_citizen", X = 120, Z = 120 },
                    new() { Template = "units/athen/infantry_spearman_b", X = 124, Z = 120 },
                    new() { Template = "gaia/tree/oak", X = 150, Z = 120 },
                    new() { Template = "gaia/tree/oak", X = 155, Z = 125 },
                },
                Triggers = new()
                {
                    new() { Name = "welcome", Type = "OnTimer", TimerSeconds = 2,
                        Message = "Welcome! Left-click to select your units." },
                    new() { Name = "tip_gather", Type = "OnTimer", TimerSeconds = 15,
                        Message = "Right-click on trees to gather wood." },
                    new() { Name = "tip_build", Type = "OnTimer", TimerSeconds = 30,
                        Message = "Select a villager and press B to build." },
                }
            };
        }

        public static CampaignScenario CreateConquest()
        {
            return new CampaignScenario
            {
                Name = "Conquest",
                Description = "Destroy the enemy civilization.",
                Entities = new()
                {
                    new() { Template = "structures/athen/civil_centre", X = 120, Z = 120 },
                    new() { Template = "units/athen/infantry_spearman_b", X = 132, Z = 120 },
                    new() { Template = "units/athen/infantry_spearman_b", X = 136, Z = 120 },
                    new() { Template = "units/athen/cavalry_swordsman_b", X = 140, Z = 120 },
                    new() { Template = "structures/rome/civil_centre", X = 220, Z = 220 },
                    new() { Template = "units/rome/infantry_spearman_b", X = 232, Z = 220 },
                    new() { Template = "units/rome/infantry_spearman_b", X = 236, Z = 220 },
                },
                Triggers = new()
                {
                    new() { Name = "objective", Type = "OnTimer", TimerSeconds = 1,
                        Message = "Destroy the Roman Town Center to win!" },
                }
            };
        }

        public event Action<string>? OnScenarioMessage;

        /// <summary>把 ScenarioTrigger 数据转换为数据驱动 TriggerDefinition 注册进系统。
        /// 教程的 OnTimer 消息 → TimeElapsed 条件 + ShowMessage 动作(一次性)。
        /// 消息出口经 TriggerSystem.Sink(ITriggerSink);同时保留 OnScenarioMessage 事件:
        /// 若订阅存在,包一层 sink 透传。</summary>
        public void ApplyTriggers(TriggerSystem system)
        {
            if (OnScenarioMessage != null)
            {
                var prev = system.Sink;
                system.Sink = new MessageRelaySink(prev, msg => OnScenarioMessage(msg));
            }
            foreach (var t in Triggers)
            {
                if (t.Type == "OnTimer" && t.TimerSeconds.HasValue)
                {
                    system.Add(new TriggerDefinition
                    {
                        Name = t.Name,
                        Once = true,
                        Conditions =
                        {
                            new TriggerCondition { Type = "TimeElapsed",
                                Params = { ["Seconds"] = t.TimerSeconds.Value.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture) } }
                        },
                        Actions =
                        {
                            new TriggerAction { Type = "ShowMessage",
                                Params = { ["Text"] = t.Message } }
                        }
                    });
                }
            }
        }

        private sealed class MessageRelaySink : ITriggerSink
        {
            private readonly ITriggerSink? _inner;
            private readonly Action<string> _onMessage;
            public MessageRelaySink(ITriggerSink? inner, Action<string> onMessage)
            { _inner = inner; _onMessage = onMessage; }
            public void ShowMessage(string text)
            { _inner?.ShowMessage(text); _onMessage(text); }
            public IReadOnlyList<EntityId> SpawnEntities(string template, int playerId, float x, float z, int count, float spread)
                => _inner?.SpawnEntities(template, playerId, x, z, count, spread)
                    ?? (IReadOnlyList<EntityId>)Array.Empty<EntityId>();
        }
    }

    public sealed class ScenarioEntity
    {
        public string Template = "";
        public float X;
        public float Z;
    }

    public sealed class ScenarioTrigger
    {
        public string Name = "";
        public string Type = "";
        public float? TimerSeconds;
        public int? TurnNumber;
        public uint? EntityId;
        public string Message = "";
    }
}

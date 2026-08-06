using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Events;

namespace ZeroAD.Sim.Components
{
    /// <summary>终局管理器(原版 EndGameManager.js 的移植):胜利条件体系。
    /// 原版胜利条件(地图 ScriptSettings.VictoryConditions;空 = ["conquest"]):
    ///   conquest            全歼敌军单位+建筑(默认,原版 Conquest.js)
    ///   conquest_units      仅歼灭敌军单位(不含建筑)
    ///   conquest_civic_centers 仅摧毁敌军市政中心类建筑
    ///   wonder              建成并守住奇观 WonderVictoryDuration 秒(原版 Wonder.js)
    ///   capture_the_relic   持有圣物 RelicVictoryDuration 秒(同机制,Relic 类实体)
    ///   ceasefire           停战时间到 → 全体存活玩家共胜(原版 Ceasefire.js)
    /// ComponentManager.TickVictory 每回合驱动;WonderVictory 计时在本类内。</summary>
    public sealed class EndGameManager
    {
        /// <summary>当前胜利条件(空 = 默认征服)。</summary>
        public List<string> VictoryConditions = new();
        /// <summary>奇观胜利所需守住秒数(原版游戏设置,默认 600s)。</summary>
        public float WonderVictoryDuration = 600f;
        /// <summary>圣物持有胜利所需秒数(默认同奇观)。</summary>
        public float RelicVictoryDuration = 600f;
        /// <summary>停战胜利时长(秒;<=0 表示不启用)。</summary>
        public float CeasefireDuration = 0f;

        // 奇观/圣物计时(>0 = 计时中)
        private float _wonderTimer;
        private int _wonderOwner = -1;
        private float _relicTimer;
        private int _relicOwner = -1;
        private float _ceasefireElapsed;

        public bool HasCondition(string name) =>
            VictoryConditions.Count == 0
                ? name == "conquest"
                : VictoryConditions.Contains(name, StringComparer.Ordinal);

        /// <summary>从地图 ScriptSettings 注入胜利条件(空表 = 保持默认征服)。</summary>
        public void SetVictoryConditions(IEnumerable<string> conditions)
        {
            VictoryConditions = conditions.ToList();
        }

        /// <summary>每回合终局推进(由 ComponentManager.TickVictory 在征服变体检查后调用)。
        /// 返回 true = 本回合产生了胜者(比赛结束)。</summary>
        public bool Tick(ComponentManager cm, float dt)
        {
            // 奇观胜利(原版 Wonder.js):任一玩家持有 Wonder 类建筑 → 倒计时;
            // 倒计时归零 → 该玩家胜。奇观被毁 → 取消计时。
            if (HasCondition("wonder"))
            {
                int owner = FindOwnerWithClass(cm, "Wonder");
                if (owner >= 0)
                {
                    if (_wonderOwner != owner) { _wonderOwner = owner; _wonderTimer = 0; }
                    _wonderTimer += dt;
                    if (_wonderTimer >= WonderVictoryDuration)
                    {
                        DeclareWinner(cm, _wonderOwner, "Held the wonder until victory.");
                        return true;
                    }
                }
                else if (_wonderOwner >= 0)
                {
                    _wonderOwner = -1;
                    _wonderTimer = 0;   // 奇观被毁,计时作废
                }
            }

            // 圣物胜利(同机制,Relic 类实体)
            if (HasCondition("capture_the_relic"))
            {
                int owner = FindOwnerWithClass(cm, "Relic");
                if (owner >= 0)
                {
                    if (_relicOwner != owner) { _relicOwner = owner; _relicTimer = 0; }
                    _relicTimer += dt;
                    if (_relicTimer >= RelicVictoryDuration)
                    {
                        DeclareWinner(cm, _relicOwner, "Held the relic until victory.");
                        return true;
                    }
                }
                else if (_relicOwner >= 0)
                {
                    _relicOwner = -1;
                    _relicTimer = 0;
                }
            }

            // 停战胜利(原版 Ceasefire.js):时长到 → 全体存活玩家共胜。
            if (HasCondition("ceasefire") && CeasefireDuration > 0)
            {
                _ceasefireElapsed += dt;
                if (_ceasefireElapsed >= CeasefireDuration)
                {
                    foreach (int pid in cm.Players.GetNonGaiaPlayerIds())
                    {
                        var player = cm.Players.GetPlayerEntity(pid);
                        if (player != null && player.IsActive())
                        {
                            player.SetWon();
                            cm.Events.RaisePlayerWon(new PlayerWonEvent { PlayerId = pid });
                        }
                    }
                    return true;
                }
            }

            return false;
        }

        /// <summary>找持有指定 class 实体的非 gaia 玩家(首个;-1 = 无)。</summary>
        private static int FindOwnerWithClass(ComponentManager cm, string className)
        {
            var range = SimSystem.Range;
            if (range == null) return -1;
            foreach (int pid in cm.Players.GetNonGaiaPlayerIds())
            {
                foreach (var entity in range.GetEntitiesByPlayer(pid))
                {
                    var id = cm.QueryInterface<IdentityComponent>(entity);
                    if (id != null && id.HasClass(className)) return pid;
                }
            }
            return -1;
        }

        private static void DeclareWinner(ComponentManager cm, int playerId, string reason)
        {
            var player = cm.Players.GetPlayerEntity(playerId);
            if (player != null && player.SetWon())
                cm.Events.RaisePlayerWon(new PlayerWonEvent { PlayerId = playerId });
        }
    }
}

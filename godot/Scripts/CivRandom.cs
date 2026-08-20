using System;
using System.Collections.Generic;
using ZeroAD.Sim.Net;

namespace ZeroAD.Godot;

/// <summary>开局前把槽位的 "random" 文明解析成具体文明——原版 gamesettings/attributes/
/// PlayerCiv.js 的 pickRandomItems():GUI 侧(Math.random,非确定性)抽签后烘焙进
/// PlayerData,sim 永远见不到 "random"。本侧同理:SP 在选图面板 Start、MP 在 host
/// 冻结槽位时解析,之后进入 InitWorld/SkirmishReplacer 的都是真文明代码。
/// 不解析的后果:skirmish 占位替换 general 兜底出 structures/random/civil_centre
/// 等不存在的模板 → CC/起始单位全部消失(Gold Oasis 实测)。
/// 可选文明表 = SelectableInGameSetup 的 15 文明(与选图面板 Civs 同源)。</summary>
public static class CivRandom
{
    public static readonly string[] SelectableCivs =
    {
        "athen", "brit", "cart", "gaul", "germ", "han", "iber",
        "kush", "mace", "maur", "ptol", "rome", "sele", "spart", "achae",
    };

    private static readonly Random Rng = new();

    /// <summary>返回新槽位表:Civ=="random" 的非 Closed 槽换成随机真文明,其余原样。
    /// 上游逐槽独立抽签(不是去重分配),同图允许撞文明。</summary>
    public static List<PlayerSlotSetup> Resolve(IReadOnlyList<PlayerSlotSetup> slots)
    {
        var resolved = new List<PlayerSlotSetup>(slots.Count);
        foreach (var s in slots)
        {
            resolved.Add(s.Kind != PlayerSlotKind.Closed && s.Civ == "random"
                ? s with { Civ = SelectableCivs[Rng.Next(SelectableCivs.Length)] }
                : s);
        }
        return resolved;
    }
}

using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>
/// Per-player diplomacy stances toward every other player. Ported from Diplomacy.js's
/// <c>m_Diplomacy[player]</c> array (1 = ally, 0 = neutral, -1 = enemy). Lives on each
/// player entity alongside <see cref="PlayerComponent"/>. This is the kernel's first
/// diplomacy data; the alliance shared-LOS pathway (Pathway B of the original's
/// VisionSharing) consumes it via <see cref="PlayerManager.GetMutualAllies"/>.
///
/// Stances are one-directional: player A may consider B an ally without B reciprocating.
/// Mutual alliance (the precondition for shared LOS) is decided centrally in
/// <see cref="PlayerManager.GetMutualAllies"/>, mirroring Diplomacy.js's GetMutualAllies
/// (both directions must be ally).
/// </summary>
[Component("Diplomacy", "Diplomacy")]
public sealed class DiplomacyComponent : ComponentBase, IComponentMessageHandler
{
    public const int Ally = 1;
    public const int Neutral = 0;
    public const int Enemy = -1;

    // Index = other player id (1..MaxPlayers). Index 0 (gaia) unused. Defaults to Neutral.
    private readonly int[] _stance = new int[LosGrid.MaxPlayers + 1];

    private static bool ValidPlayer(int p) => p >= 1 && p <= LosGrid.MaxPlayers;

    public bool IsAlly(int otherPlayer) => ValidPlayer(otherPlayer) && _stance[otherPlayer] == Ally;
    public bool IsEnemy(int otherPlayer) => ValidPlayer(otherPlayer) && _stance[otherPlayer] == Enemy;

    public int GetStance(int otherPlayer) => ValidPlayer(otherPlayer) ? _stance[otherPlayer] : Neutral;

    public void SetAlly(int otherPlayer) => Set(otherPlayer, Ally);
    public void SetEnemy(int otherPlayer) => Set(otherPlayer, Enemy);
    public void SetNeutral(int otherPlayer) => Set(otherPlayer, Neutral);

    private void Set(int otherPlayer, int stance)
    {
        if (ValidPlayer(otherPlayer)) _stance[otherPlayer] = stance;
    }

    /// <summary>teamLock / ceasefire 门(原版 Diplomacy.js IsTeamLocked + CeasefireManager)。
    /// 本轮停火系统未移植,恒 false——保留门以便 GUI/执行器对齐原版 Commands.js 的拒令语义。</summary>
    public bool IsTeamLocked() => false;

    /// <summary>设我方对 <paramref name="otherId"/> 的立场,并套原版 Diplomacy.js OnDiplomacyChanged
    /// 的**单向恶化规则**:若新 stance 低于对方对我方 stance,把对方对我方降到同值(只恶化、不改善)。
    /// <paramref name="selfId"/> = 我方玩家号,<paramref name="other"/> = 对方 DiplomacyComponent,
    /// <paramref name="otherId"/> = 对方玩家号。改善方向(升级 stance)不影响对方。</summary>
    public void SetStanceToward(int selfId, DiplomacyComponent other, int otherId, int stance)
    {
        Set(otherId, stance);
        if (stance < other.GetStance(selfId))
            other.Set(selfId, stance);
    }

    public override void Serialize(ISerializer s)
    {
        // Fixed-length 16 stances (player 1..MaxPlayers), deterministic with no sort needed.
        for (int p = 1; p <= LosGrid.MaxPlayers; p++)
            s.NumberI32("stance", _stance[p]);
    }

    public override void Deserialize(IDeserializer d)
    {
        for (int p = 1; p <= LosGrid.MaxPlayers; p++)
            _stance[p] = d.NumberI32("stance");
    }

    public void HandleMessage(IMessage message) { }
}

using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Combat enemy semantics (对齐原版 Player.IsEnemy / UnitAI.CanAttack):
/// <list type="bullet">
/// <item><see cref="PlayerManager.IsEnemy"/>:self=false;gaia(0)=true;无玩家实体或无
/// DiplomacyComponent(旧世界/测试)=true(原版开局默认外交=enemy);否则按 seeded 立场;
/// Neutral=非交战国(不可攻击、不被索敌)。</item>
/// <item>UnitAI Order.Attack 敌对校验:self/盟友/中立目标拒收,敌人/gaia 放行。</item>
/// </list>
/// </summary>
public sealed class CombatEnemySemanticsTests
{
    private static EntityId AddPlayerWithDiplomacy(ComponentManager cm, int playerId)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PlayerComponent());
        cm.AddComponent(e, new OwnershipComponent { PlayerId = playerId });
        cm.AddComponent(e, new DiplomacyComponent());
        cm.Players.AddPlayer(playerId, e);
        return e;
    }

    private static EntityId MakeSoldier(ComponentManager cm, int player)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new IdentityComponent());
        cm.AddComponent(e, new UnitAIComponent());
        cm.AddComponent(e, new AttackComponent());
        // 物理型须确有伤害(原版该型不存在即跳过;零伤害=型不存在)——裸组件默认无伤害。
        cm.QueryInterface<AttackComponent>(e)!.Damage.Amounts[DamageType.Hack] = 5;
        cm.AddComponent(e, new HealthComponent());
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        return e;
    }

    private static ComponentManager NewCombatWorld()
    {
        // UnitMotion.Tick reads through SimSystem — wire it (UnitAITests.MakeUnit 同款)。
        var cm = new ComponentManager(rngSeed: 1);
        SimSystem.Init(cm);
        return cm;
    }

    private static string DispatchAttack(ComponentManager cm, EntityId attacker, EntityId target)
    {
        var ai = cm.QueryInterface<UnitAIComponent>(attacker)!;
        ai.Attack(target);
        ai.Tick(0.1f, cm);   // dispatch the Attack order
        return ai.FsmStateName;
    }

    // ---------- PlayerManager.IsEnemy 矩阵 ----------

    [Fact]
    public void IsEnemy_Self_Is_False()
    {
        var cm = NewCombatWorld();
        Assert.False(cm.Players.IsEnemy(1, 1));
    }

    [Fact]
    public void IsEnemy_Gaia_Is_True()
    {
        var cm = NewCombatWorld();
        AddPlayerWithDiplomacy(cm, 1);
        Assert.True(cm.Players.IsEnemy(1, 0));   // 原版 IsEnemy(0)=true:gaia 对全员敌对
    }

    [Fact]
    public void IsEnemy_UnregisteredSelfPlayer_Defaults_True()
    {
        var cm = NewCombatWorld();
        Assert.True(cm.Players.IsEnemy(1, 2));   // 无玩家实体 → 原版默认外交=enemy
    }

    [Fact]
    public void IsEnemy_NoDiplomacyComponent_Defaults_True()
    {
        var cm = NewCombatWorld();
        var e = cm.CreateEntity();               // 注册玩家实体但不挂 DiplomacyComponent
        cm.AddComponent(e, new PlayerComponent());
        cm.Players.AddPlayer(1, e);
        Assert.True(cm.Players.IsEnemy(1, 2));
    }

    [Fact]
    public void IsEnemy_SeededTeams_Ally_False_Enemy_True()
    {
        var cm = NewCombatWorld();
        AddPlayerWithDiplomacy(cm, 1);
        AddPlayerWithDiplomacy(cm, 2);
        AddPlayerWithDiplomacy(cm, 3);
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 1 });

        Assert.False(cm.Players.IsEnemy(1, 2));  // 同队 → 盟友
        Assert.True(cm.Players.IsEnemy(1, 3));   // 异队 → 敌人
        Assert.True(cm.Players.IsEnemy(3, 1));   // 对称
    }

    [Fact]
    public void IsEnemy_NeutralStance_Is_False()
    {
        // 中立语义:挂了 DiplomacyComponent 但未 seeding → 默认 Neutral → 非交战国。
        var cm = NewCombatWorld();
        AddPlayerWithDiplomacy(cm, 1);
        AddPlayerWithDiplomacy(cm, 2);
        Assert.False(cm.Players.IsEnemy(1, 2));
        Assert.False(cm.Players.IsEnemy(2, 1));
    }

    // ---------- UnitAI Order.Attack 敌对校验 ----------

    [Fact]
    public void AttackOrder_Rejects_Own_Target()
    {
        var cm = NewCombatWorld();
        var attacker = MakeSoldier(cm, 1);
        var own = MakeSoldier(cm, 1);
        Assert.Equal("INDIVIDUAL.IDLE", DispatchAttack(cm, attacker, own));
    }

    [Fact]
    public void AttackOrder_Rejects_Ally_Target()
    {
        var cm = NewCombatWorld();
        AddPlayerWithDiplomacy(cm, 1);
        AddPlayerWithDiplomacy(cm, 2);
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 0 });
        var attacker = MakeSoldier(cm, 1);
        var ally = MakeSoldier(cm, 2);
        Assert.Equal("INDIVIDUAL.IDLE", DispatchAttack(cm, attacker, ally));
    }

    [Fact]
    public void AttackOrder_Rejects_Neutral_Target()
    {
        var cm = NewCombatWorld();
        AddPlayerWithDiplomacy(cm, 1);
        AddPlayerWithDiplomacy(cm, 2);   // 未 seeding → Neutral
        var attacker = MakeSoldier(cm, 1);
        var neutral = MakeSoldier(cm, 2);
        Assert.Equal("INDIVIDUAL.IDLE", DispatchAttack(cm, attacker, neutral));
    }

    [Fact]
    public void AttackOrder_Allows_Enemy_Target()
    {
        var cm = NewCombatWorld();
        AddPlayerWithDiplomacy(cm, 1);
        AddPlayerWithDiplomacy(cm, 2);
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 1 });
        var attacker = MakeSoldier(cm, 1);
        var enemy = MakeSoldier(cm, 2);
        Assert.StartsWith("INDIVIDUAL.COMBAT", DispatchAttack(cm, attacker, enemy));
    }

    [Fact]
    public void AttackOrder_Allows_Gaia_Target()
    {
        // gaia(0)=敌(原版语义:可猎杀 gaia 野兽)。
        var cm = NewCombatWorld();
        AddPlayerWithDiplomacy(cm, 1);
        var attacker = MakeSoldier(cm, 1);
        var gaiaBeast = MakeSoldier(cm, 0);
        Assert.StartsWith("INDIVIDUAL.COMBAT", DispatchAttack(cm, attacker, gaiaBeast));
    }
}

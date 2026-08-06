using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using Xunit;

namespace ZeroAD.Sim.Tests;

// Tests for the player win/loss state model and the conquest victory detection.
// Covers: PlayerComponent state transitions (idempotent, mono-directional), and the
// ComponentManager.TickVictory conquest logic (defeat on zero entities, win on last standing).
public sealed class VictoryTests
{
    // --- PlayerComponent state model ---

    [Fact]
    public void Player_StartsActive()
    {
        var p = new PlayerComponent();
        Assert.Equal(PlayerState.Active, p.State);
        Assert.True(p.IsActive());
        Assert.False(p.IsDefeated());
        Assert.False(p.HasWon());
    }

    [Fact]
    public void SetDefeated_TransitionsFromActive()
    {
        var p = new PlayerComponent();
        Assert.True(p.SetDefeated());   // returns true: state changed
        Assert.True(p.IsDefeated());
        Assert.False(p.IsActive());
    }

    [Fact]
    public void SetWon_TransitionsFromActive()
    {
        var p = new PlayerComponent();
        Assert.True(p.SetWon());
        Assert.True(p.HasWon());
    }

    [Fact]
    public void SetDefeated_Idempotent_DoesNotTransitionFromDefeated()
    {
        var p = new PlayerComponent();
        p.SetDefeated();
        // A defeated player can't be re-defeated or won.
        Assert.False(p.SetDefeated());
        Assert.False(p.SetWon());
        Assert.True(p.IsDefeated());
    }

    [Fact]
    public void SetWon_Idempotent_DoesNotTransitionFromWon()
    {
        var p = new PlayerComponent();
        p.SetWon();
        Assert.False(p.SetWon());
        Assert.False(p.SetDefeated());
        Assert.True(p.HasWon());
    }

    // --- Conquest detection (ComponentManager.TickVictory) ---

    private static EntityId MakeUnit(ComponentManager cm, int owner, string name = "Unit")
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        cm.AddComponent(e, new IdentityComponent { Name = name, IsUnit = true });
        cm.AddComponent(e, new HealthComponent { Current = 100, Max = 100 });
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        // RangeManager only counts entities that are InWorld and have a registered owner. The
        //real spawn path (SpawnEntity) fires EntityCreated + OwnerChanged + PositionChanged;
        //this test uses CreateEntity directly, so fire the same notifications manually.
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, owner);
        var origin = new ZeroAD.Sim.Maths.FixedVector2D(ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.Zero);
        cm.NotifyPositionChanged(e, origin, origin);
        return e;
    }

    private static ComponentManager SetupTwoPlayerWorld()
    {
        var cm = new ComponentManager(rngSeed: 1);
        Components.SimSystem.Init(cm);
        // Player 1
        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent());
        cm.Players.AddPlayer(1, p1);
        // Player 2
        var p2 = cm.CreateEntity();
        cm.AddComponent(p2, new PlayerComponent());
        cm.Players.AddPlayer(2, p2);
        // Wire a RangeManager so TickVictory can count entities by player.
        var range = new Components.RangeManager(cm, ZeroAD.Sim.Maths.Fixed.FromInt(256), ZeroAD.Sim.Maths.Fixed.FromInt(256));
        Components.SimSystem.SetRangeManager(range);
        return cm;
    }

    [Fact]
    public void TickVictory_NoDefeat_WhenBothPlayersHaveUnits()
    {
        var cm = SetupTwoPlayerWorld();
        MakeUnit(cm, owner: 1);
        MakeUnit(cm, owner: 2);

        cm.TickVictory();

        Assert.True(cm.Players.GetPlayerEntity(1)!.IsActive());
        Assert.True(cm.Players.GetPlayerEntity(2)!.IsActive());
        Assert.False(cm.IsGameOver);
    }

    [Fact]
    public void TickVictory_Defeat_WhenPlayerLosesAllUnits()
    {
        var cm = SetupTwoPlayerWorld();
        var p1Unit = MakeUnit(cm, owner: 1);
        MakeUnit(cm, owner: 2);

        // Player 1's unit dies.
        cm.QueryInterface<HealthComponent>(p1Unit)!.Current = 0;
        // SimBridge.RemoveDeadEntities destroys dead entities + fires OwnershipChanged (To=-1)
        // so the RangeManager drops them. Replicate that here for the kernel test.
        cm.NotifyOwnerChanged(p1Unit, 1, -1);
        cm.DestroyEntity(p1Unit);

        cm.TickVictory();

        Assert.True(cm.Players.GetPlayerEntity(1)!.IsDefeated());
        // Player 2 still has a unit → not yet won (2 players, 1 defeated, but TickVictory only
        // crowns a winner when exactly one ACTIVE player remains; player 2 is the sole active).
        Assert.True(cm.Players.GetPlayerEntity(2)!.HasWon());
        Assert.True(cm.IsGameOver);
    }

    [Fact]
    public void TickVictory_GameOver_StopsAfterWinnerCrowned()
    {
        var cm = SetupTwoPlayerWorld();
        var p1Unit = MakeUnit(cm, owner: 1);
        MakeUnit(cm, owner: 2);

        cm.QueryInterface<HealthComponent>(p1Unit)!.Current = 0;
        cm.NotifyOwnerChanged(p1Unit, 1, -1);
        cm.DestroyEntity(p1Unit);

        cm.TickVictory();   // player 1 defeated, player 2 wins, IsGameOver = true
        // Calling again must not re-fire or change anything.
        var p2State = cm.Players.GetPlayerEntity(2)!.State;
        cm.TickVictory();
        Assert.Equal(p2State, cm.Players.GetPlayerEntity(2)!.State);
    }

    [Fact]
    public void TickVictory_RaisesPlayerDefeatedEvent()
    {
        var cm = SetupTwoPlayerWorld();
        int? defeatedId = null;
        cm.Events.PlayerDefeated += e => defeatedId = e.PlayerId;

        var p1Unit = MakeUnit(cm, owner: 1);
        MakeUnit(cm, owner: 2);

        cm.QueryInterface<HealthComponent>(p1Unit)!.Current = 0;
        cm.NotifyOwnerChanged(p1Unit, 1, -1);
        cm.DestroyEntity(p1Unit);

        cm.TickVictory();

        Assert.Equal(1, defeatedId);
    }

    // --- Victory-condition variants (EndGameManager) ---

    private static EntityId MakeBuilding(ComponentManager cm, int owner, params string[] classes)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        var id = new IdentityComponent { Name = "Bld", IsUnit = false, IsBuilding = true };
        id.Classes.AddRange(classes);
        cm.AddComponent(e, id);
        cm.AddComponent(e, new HealthComponent { Current = 100, Max = 100 });
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, owner);
        var origin = new ZeroAD.Sim.Maths.FixedVector2D(ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.Zero);
        cm.NotifyPositionChanged(e, origin, origin);
        return e;
    }

    private static void Kill(EntityId e, int owner, ComponentManager cm)
    {
        cm.QueryInterface<HealthComponent>(e)!.Current = 0;
        cm.NotifyOwnerChanged(e, owner, -1);
        cm.DestroyEntity(e);
    }

    [Fact]
    public void ConquestUnits_PlayerWithOnlyBuildings_IsDefeated()
    {
        var cm = SetupTwoPlayerWorld();
        cm.EndGame.SetVictoryConditions(new[] { "conquest_units" });
        MakeBuilding(cm, owner: 1);                     // player 1: only a building, no units
        MakeUnit(cm, owner: 2);

        cm.TickVictory();

        Assert.True(cm.Players.GetPlayerEntity(1)!.IsDefeated());
        Assert.True(cm.Players.GetPlayerEntity(2)!.HasWon());
    }

    [Fact]
    public void ConquestUnits_PlayerWithUnits_SurvivesWithoutBuildings()
    {
        var cm = SetupTwoPlayerWorld();
        cm.EndGame.SetVictoryConditions(new[] { "conquest_units" });
        MakeUnit(cm, owner: 1);                         // no buildings, but a unit
        MakeUnit(cm, owner: 2);

        cm.TickVictory();

        Assert.True(cm.Players.GetPlayerEntity(1)!.IsActive());
        Assert.False(cm.IsGameOver);
    }

    [Fact]
    public void ConquestCivicCentres_DefeatOnlyWhenAllCentresLost()
    {
        var cm = SetupTwoPlayerWorld();
        cm.EndGame.SetVictoryConditions(new[] { "conquest_civic_centers" });
        var cc = MakeBuilding(cm, owner: 1, "CivCentre");
        MakeBuilding(cm, owner: 1, "House");            // other buildings don't count
        MakeBuilding(cm, owner: 2, "CivCentre");

        cm.TickVictory();
        // Both players have a civic centre → nobody defeated.
        Assert.True(cm.Players.GetPlayerEntity(1)!.IsActive());

        // Player 1 loses the civic centre; the house alone cannot save them.
        Kill(cc, 1, cm);
        cm.TickVictory();

        Assert.True(cm.Players.GetPlayerEntity(1)!.IsDefeated());
        Assert.True(cm.Players.GetPlayerEntity(2)!.HasWon());
        Assert.True(cm.IsGameOver);
    }

    [Fact]
    public void WonderVictory_HoldingWonderForDuration_Wins()
    {
        var cm = SetupTwoPlayerWorld();
        cm.EndGame.SetVictoryConditions(new[] { "conquest", "wonder" });
        cm.EndGame.WonderVictoryDuration = 5f;          // 5s for the test
        MakeBuilding(cm, owner: 1, "Wonder");
        MakeUnit(cm, owner: 2);

        // 40 ticks × 0.1s = 4.0s < 5s → no winner yet (0.1f 累加有 float 误差,留足边界余量)。
        for (int i = 0; i < 40; i++) cm.TickVictory();
        Assert.False(cm.IsGameOver);

        for (int i = 0; i < 30; i++) cm.TickVictory();  // 累计 7.0s > 5s → 奇观胜利
        Assert.True(cm.Players.GetPlayerEntity(1)!.HasWon());
        Assert.True(cm.IsGameOver);
    }

    [Fact]
    public void WonderVictory_WonderDestroyed_ResetsCountdown()
    {
        var cm = SetupTwoPlayerWorld();
        cm.EndGame.SetVictoryConditions(new[] { "conquest", "wonder" });
        cm.EndGame.WonderVictoryDuration = 5f;
        var wonder = MakeBuilding(cm, owner: 1, "Wonder");
        MakeUnit(cm, owner: 2);

        for (int i = 0; i < 30; i++) cm.TickVictory();  // 3s of countdown
        Kill(wonder, 1, cm);
        // Wonder gone; player 1 now has no conquest entities → conquest would defeat them.
        // Give player 1 a regular unit so the game continues with no wonder on the field.
        MakeUnit(cm, owner: 1);

        for (int i = 0; i < 100; i++) cm.TickVictory(); // far past the old countdown
        Assert.False(cm.IsGameOver);
        Assert.True(cm.Players.GetPlayerEntity(1)!.IsActive());
    }

    [Fact]
    public void Ceasefire_AllActivePlayersCoWin_WhenTimerExpires()
    {
        var cm = SetupTwoPlayerWorld();
        cm.EndGame.SetVictoryConditions(new[] { "ceasefire" });
        cm.EndGame.CeasefireDuration = 2f;
        MakeUnit(cm, owner: 1);
        MakeUnit(cm, owner: 2);

        for (int i = 0; i < 15; i++) cm.TickVictory();  // 1.5s → nothing yet
        Assert.False(cm.IsGameOver);

        for (int i = 0; i < 10; i++) cm.TickVictory();  // 累计 2.5s > 2s → both co-win
        Assert.True(cm.Players.GetPlayerEntity(1)!.HasWon());
        Assert.True(cm.Players.GetPlayerEntity(2)!.HasWon());
        Assert.True(cm.IsGameOver);
    }

    [Fact]
    public void RelicVictory_HoldingRelicForDuration_Wins()
    {
        var cm = SetupTwoPlayerWorld();
        cm.EndGame.SetVictoryConditions(new[] { "capture_the_relic" });
        cm.EndGame.RelicVictoryDuration = 3f;
        MakeBuilding(cm, owner: 1, "Relic");
        MakeUnit(cm, owner: 2);

        for (int i = 0; i < 20; i++) cm.TickVictory();  // 2.0s < 3s → nothing yet
        Assert.False(cm.IsGameOver);
        for (int i = 0; i < 20; i++) cm.TickVictory();  // 累计 4.0s > 3s → 圣物胜利
        Assert.True(cm.Players.GetPlayerEntity(1)!.HasWon());
        Assert.True(cm.IsGameOver);
    }
}

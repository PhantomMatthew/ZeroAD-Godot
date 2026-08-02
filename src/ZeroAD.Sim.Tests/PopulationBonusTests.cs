using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Population bonus pipeline: A26+ data declares it as top-level &lt;Population&gt;&lt;Bonus&gt;
/// (not &lt;Cost&gt;&lt;PopulationBonus&gt; — that node name does not exist in this data version,
/// which silently zeroed every building's bonus and made pop cap impossible to raise).
/// Regression: "built 4 houses, still pop-limited" (tutorial smoke 2026-08-02).
/// </summary>
public class PopulationBonusTests
{
    /// <summary>Anchor to the repo root by walking up from the test assembly — a bare
    /// relative "../../../binaries" resolves against bin/Debug/net8.0 and silently misses
    /// (see test-suite-silent-skips). Returns null when the data tree is absent.</summary>
    private static TemplateLoader? TryLoadTemplates()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = System.IO.Path.Combine(dir.FullName, "binaries/data/mods/public/simulation/templates");
            if (System.IO.Directory.Exists(candidate))
                return new TemplateLoader(candidate);
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void House_ParsesTopLevelPopulationBonus()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return; // data tree absent (LFS not pulled)

        var stats = templates.ExtractStats("structures/athen/house");
        Assert.True(stats.PopulationBonus > 0,
            $"house must grant pop bonus from <Population><Bonus> (got {stats.PopulationBonus})");
    }

    [Fact]
    public void CivilCentre_ParsesTopLevelPopulationBonus()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var stats = templates.ExtractStats("structures/athen/civil_centre");
        Assert.True(stats.PopulationBonus >= 20,
            $"civil centre grants +20 pop in data (got {stats.PopulationBonus})");
    }

    [Fact]
    public void OwnershipChange_RecomputesPopBonus_WhenBuildingLost()
    {
        // 房子被毁/易主后加成立即消失(原版 MT_OwnershipChanged 全量刷);此前只在
        // 地基完工时重算,被毁的房子永久赠送人口。
        var cm = new ComponentManager(42);
        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent());
        cm.RegisterPlayer(1, playerEntity);
        var player = cm.GetPlayerEntity(1)!;

        var house = cm.CreateEntity();
        cm.AddComponent(house, new PopulationComponent { Bonus = 5 });
        cm.AddComponent(house, new OwnershipComponent { PlayerId = 1 });

        cm.RecomputePlayerPopBonus(1);
        Assert.Equal(5, player.PopBonuses);

        // 真实流程:实体先销毁/易主,再发 ownership-changed 通知(重算读的是当前归属)。
        cm.DestroyEntity(house);
        cm.Players.ApplyOwnershipPopChange(house, oldOwner: 1, newOwner: -1);
        Assert.Equal(0, player.PopBonuses);
    }
}

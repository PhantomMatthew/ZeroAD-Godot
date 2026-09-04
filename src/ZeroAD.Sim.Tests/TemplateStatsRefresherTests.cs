using System.IO;
using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>模板 hotload 存量实体重灌(TemplateStatsRefresher)测试:
/// 改文件 → Invalidate → 重灌 → 在役实体组件字段按新模板更新(血量/攻击/驻军)。</summary>
public sealed class TemplateStatsRefresherTests
{
    private static (ComponentManager cm, TemplateLoader loader, string dir) World(
        string templateXml)
    {
        string dir = Path.Combine(Path.GetTempPath(), "zad_refresh_" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(dir, "units", "athen"));
        File.WriteAllText(Path.Combine(dir, "units", "athen", "hoplite.xml"), templateXml);
        var loader = new TemplateLoader(dir);
        loader.LoadAllTemplates();
        var cm = new ComponentManager(rngSeed: 1, templates: loader);
        SimSystem.Init(cm);
        return (cm, loader, dir);
    }

    private const string V1 = """
        <Entity>
          <Identity><Civ>athen</Civ><Classes datatype="tokens">Infantry Soldier</Classes></Identity>
          <Health><Max>100</Max><RegenRate>0</RegenRate><IdleRegenRate>0</IdleRegenRate><DeathType>corpse</DeathType><Unhealable>false</Unhealable></Health>
          <Attack><Melee><Damage><Hack>10</Hack></Damage><MaxRange>2</MaxRange><RepeatTime>1000</RepeatTime></Melee></Attack>
          <Cost><Resources><food>50</food></Resources><BuildTime>10</BuildTime><Population>1</Population></Cost>
        </Entity>
        """;

    private const string V2 = """
        <Entity>
          <Identity><Civ>athen</Civ><Classes datatype="tokens">Champion Infantry Soldier</Classes></Identity>
          <Health><Max>200</Max><RegenRate>0</RegenRate><IdleRegenRate>0</IdleRegenRate><DeathType>corpse</DeathType><Unhealable>false</Unhealable></Health>
          <Attack><Melee><Damage><Hack>25</Hack></Damage><MaxRange>2</MaxRange><RepeatTime>1000</RepeatTime></Melee></Attack>
          <Cost><Resources><food>50</food></Resources><BuildTime>10</BuildTime><Population>1</Population></Cost>
        </Entity>
        """;

    private static EntityId Spawn(ComponentManager cm)
    {
        var e = cm.SpawnEntity("units/athen/hoplite", 5f, 5f, ownerPlayerId: 1);
        return e;
    }

    [Fact]
    public void Refresh_ReappliesHealthAttackClasses()
    {
        var (cm, loader, dir) = World(V1);
        var e = Spawn(cm);
        var hp = cm.QueryInterface<HealthComponent>(e)!;
        var atk = cm.QueryInterface<AttackComponent>(e)!;
        Assert.Equal(100, hp.Max);
        Assert.Equal(10, atk.Types[0].Damage.Amounts[DamageType.Hack]);

        File.WriteAllText(Path.Combine(dir, "units", "athen", "hoplite.xml"), V2);
        loader.Invalidate("units/athen/hoplite");
        int refreshed = TemplateStatsRefresher.RefreshAllEntitiesWithTemplate(
            cm, loader, "units/athen/hoplite");

        Assert.True(refreshed > 0);
        Assert.Equal(200, hp.Max);
        Assert.Equal(25, atk.Types[0].Damage.Amounts[DamageType.Hack]);
        Assert.Contains("Champion", cm.QueryInterface<IdentityComponent>(e)!.Classes);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Refresh_OnlyTouchesMatchingTemplate()
    {
        var (cm, loader, dir) = World(V1);
        var e = Spawn(cm);
        var other = cm.SpawnEntity("units/athen/hoplite", 10f, 10f, ownerPlayerId: 1);
        cm.QueryInterface<IdentityComponent>(other)!.TemplateName = "units/other";

        File.WriteAllText(Path.Combine(dir, "units", "athen", "hoplite.xml"), V2);
        loader.Invalidate("units/athen/hoplite");
        TemplateStatsRefresher.RefreshAllEntitiesWithTemplate(cm, loader, "units/athen/hoplite");

        Assert.Equal(200, cm.QueryInterface<HealthComponent>(e)!.Max);
        Assert.Equal(100, cm.QueryInterface<HealthComponent>(other)!.Max);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Refresh_HealthMaxScalesCurrent()
    {
        var (cm, loader, dir) = World(V1);
        var e = Spawn(cm);
        var hp = cm.QueryInterface<HealthComponent>(e)!;
        hp.Current = 50;   // 50/100 = 50%

        File.WriteAllText(Path.Combine(dir, "units", "athen", "hoplite.xml"), V2);
        loader.Invalidate("units/athen/hoplite");
        TemplateStatsRefresher.RefreshAllEntitiesWithTemplate(cm, loader, "units/athen/hoplite");

        Assert.Equal(200, hp.Max);
        Assert.Equal(100, hp.Current);   // 比例保持 50%
        Directory.Delete(dir, true);
    }
}

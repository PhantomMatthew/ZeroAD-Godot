using System.IO;
using Xunit;
using ZeroAD.Sim.Content;

namespace ZeroAD.Sim.Tests;

/// <summary>场景 XML 预驻防/预占炮塔解析(原版 MapReader 的 &lt;Garrison&gt;/
/// &lt;Turrets&gt; 子元素)。</summary>
public sealed class ScenarioInitGarrisonTests
{
    [Fact]
    public void ParsesGarrisonAndTurretsChildren()
    {
        string xml = """
<?xml version="1.0" encoding="utf-8"?>
<Scenario>
 <Entities>
  <Entity uid="21">
    <Template>units/athen/ship_trireme</Template>
    <Player>1</Player>
    <Position x="100" y="50" z="200"/>
    <Garrison>
      <GarrisonedEntity uid="78"/>
      <GarrisonedEntity uid="79"/>
    </Garrison>
  </Entity>
  <Entity uid="22">
    <Template>structures/athen/wall_tower</Template>
    <Player>1</Player>
    <Position x="10" y="0" z="20"/>
    <Turrets>
      <TurretPoint turret="One" uid="88"/>
    </Turrets>
  </Entity>
  <Entity uid="78">
    <Template>units/athen/infantry_spearman_b</Template>
    <Player>1</Player>
    <Position x="100" y="50" z="200"/>
  </Entity>
 </Entities>
</Scenario>
""";
        string path = Path.Combine(Path.GetTempPath(), "zad_initgarrison_" + Path.GetRandomFileName() + ".xml");
        File.WriteAllText(path, xml);
        try
        {
            var data = ScenarioLoader.Load(path);
            var ship = data.Entities.Find(e => e.Uid == 21)!;
            Assert.Equal(new uint[] { 78, 79 }, ship.InitGarrisonUids);
            var tower = data.Entities.Find(e => e.Uid == 22)!;
            Assert.Single(tower.InitTurretPairs);
            Assert.Equal("One", tower.InitTurretPairs[0].Point);
            Assert.Equal(88u, tower.InitTurretPairs[0].Uid);
            var plain = data.Entities.Find(e => e.Uid == 78)!;
            Assert.Empty(plain.InitGarrisonUids);
            Assert.Empty(plain.InitTurretPairs);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

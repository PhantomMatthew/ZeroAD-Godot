using Xunit;
using ZeroAD.Sim.Content;

namespace ZeroAD.Sim.Tests;

/// <summary>actor| 合成模板(原版 ConstructTemplateActor)+ Trainer/Entities 引用校验。</summary>
public sealed class ActorTemplateTests
{
    private static string? FindRepoPath(string relative)
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : System.IO.Path.Combine(dir.FullName, relative);
    }

    [Fact]
    public void ActorPipe_SynthesizesFromSpecialActor()
    {
        string? root = FindRepoPath("binaries/data/mods/public/simulation/templates");
        if (root == null) return;
        var loader = new TemplateLoader(root);
        var node = loader.LoadTemplate("actor|units/athen_infantry_spearman_b.xml");
        // 继承 special/actor(Identity 在)+ 覆盖 VisualActor/Actor。
        Assert.Equal("units/athen_infantry_spearman_b.xml",
            node.GetChild("VisualActor").GetChild("Actor").ToString());
        Assert.True(node.GetChild("VisualActor").GetChild("ActorOnly").IsOk);
        Assert.True(node.GetChild("Footprint").GetChild("Circle").IsOk);
        Assert.True(node.GetChild("Selectable").GetChild("EditorOnly").IsOk);
    }

    [Fact]
    public void Validator_ChecksTrainerEntities()
    {
        // 自含夹具:模板引用不存在训练目标 → 报 ref;存在 → 净。
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "zad_validator_" + System.IO.Path.GetRandomFileName());
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "c.xml"),
                "<Entity><Trainer><Entities datatype=\"tokens\">units/a units/missing</Entities></Trainer></Entity>");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "units"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "units", "a.xml"),
                "<Entity><Identity><Civ>athen</Civ></Identity></Entity>");
            var loader = new TemplateLoader(dir);
            loader.LoadAllTemplates();
            var issues = TemplateValidator.ValidateAll(loader);
            Assert.Contains(issues, i => i.Kind == "ref" && i.Detail.Contains("units/missing"));
            Assert.DoesNotContain(issues, i => i.Detail.Contains("units/a'"));
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }
}

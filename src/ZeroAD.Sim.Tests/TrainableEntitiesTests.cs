using System.Linq;
using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 训练列表数据驱动(原版 Trainer.js):建筑可训练列表来自模板 Trainer/Entities,
/// 跨继承链按 datatype="tokens" 语义合并(父保留+子追加+"-token"删除),{civ}=属主
/// 文明、{native}=模板原生文明,不存在的模板过滤。此前全文明硬编码 units/spart/*,
/// 雅典 CC 出斯巴达兵。
/// </summary>
public class TrainableEntitiesTests
{
    /// <summary>锚定仓库根(避免 ../../../binaries 从 bin/Debug/net8.0 解析失败导致
    /// 数据用例静默跳过——见 test-suite-silent-skips)。数据树缺失返回 null。</summary>
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

    private static ComponentManager BuildWorld(TemplateLoader templates, string ownerCiv,
        out EntityId trainer, string trainableTokens, string nativeCiv)
    {
        var cm = new ComponentManager(42);
        cm.Templates = templates;

        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent
        {
            Wood = 10000, Food = 10000, Stone = 10000, Metal = 10000,
            PopBonuses = 300, Civ = ownerCiv,
        });
        cm.RegisterPlayer(1, playerEntity);

        trainer = cm.CreateEntity();
        cm.AddComponent(trainer, new PositionComponent());
        cm.AddComponent(trainer, new ProductionQueue
        {
            TrainableTokens = trainableTokens,
            NativeCiv = nativeCiv,
        });
        cm.AddComponent(trainer, new OwnershipComponent { PlayerId = 1 });
        return cm;
    }

    [Fact]
    public void CivilCentre_TrainableEntities_MergedAcrossInheritance()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var stats = templates.ExtractStats("structures/athen/civil_centre");
        // 父 template_structure_civic_civil_centre 的 units/{native}/support_civilian
        // 必须保留(token 合并不是覆盖),子的 3 个 {civ} 兵追加在后。
        var tokens = stats.TrainableEntities.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("units/{native}/support_civilian", tokens[0]);
        Assert.Contains("units/{civ}/infantry_spearman_b", tokens);
        Assert.Contains("units/{civ}/infantry_slinger_b", tokens);
        Assert.Contains("units/{civ}/cavalry_javelineer_b", tokens);
        Assert.Equal("athen", stats.Civ);
    }

    [Fact]
    public void Resolve_SubstitutesCivAndNative_AndFiltersMissing()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var stats = templates.ExtractStats("structures/athen/civil_centre");
        var cm = BuildWorld(templates, ownerCiv: "athen", out var trainer,
            stats.TrainableEntities, stats.Civ);
        var queue = cm.QueryInterface<ProductionQueue>(trainer)!;

        var list = queue.GetTrainableEntities(cm);
        Assert.Equal(new[]
        {
            "units/athen/support_civilian",
            "units/athen/infantry_spearman_b",
            "units/athen/infantry_slinger_b",
            "units/athen/cavalry_javelineer_b",
        }, list.ToArray());
    }

    [Fact]
    public void Resolve_NativeStaysTemplateCiv_WhenOwnerCivDiffers()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        // 占领语义(原版 Trainer.js):{native}=建筑原生文明不变,{civ}=属主文明跟随。
        var stats = templates.ExtractStats("structures/athen/civil_centre");
        var cm = BuildWorld(templates, ownerCiv: "iber", out var trainer,
            stats.TrainableEntities, stats.Civ);
        var queue = cm.QueryInterface<ProductionQueue>(trainer)!;

        var list = queue.GetTrainableEntities(cm);
        Assert.Contains("units/athen/support_civilian", list);  // {native} → athen 不变
        Assert.Contains("units/iber/infantry_spearman_b", list); // {civ} → 属主 iber
        Assert.DoesNotContain(list, t => t.Contains('{'));
    }

    [Fact]
    public void Barracks_FiltersUnitsTheCivLacks()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        // 通用兵营列表含 9 种步兵,athen 数据只有 spearman/javelineer/slinger/archer —
        // clubman/maceman 等必须被 TemplateExists 过滤(原版同)。
        var stats = templates.ExtractStats("structures/athen/barracks");
        var cm = BuildWorld(templates, ownerCiv: "athen", out var trainer,
            stats.TrainableEntities, stats.Civ);
        var queue = cm.QueryInterface<ProductionQueue>(trainer)!;

        var list = queue.GetTrainableEntities(cm);
        Assert.Contains("units/athen/infantry_spearman_b", list);
        Assert.Contains("units/athen/infantry_archer_b", list);
        Assert.DoesNotContain(list, t => t.Contains("clubman"));
        // 每项都真实存在。
        foreach (var t in list)
            Assert.True(templates.TemplateExists(t), $"{t} must exist");
    }

    [Fact]
    public void House_TrainsHouseWomanVariant()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        // A26+:房子训练 support_civilian_house(女人房屋变体)。
        var stats = templates.ExtractStats("structures/athen/house");
        var cm = BuildWorld(templates, ownerCiv: "athen", out var trainer,
            stats.TrainableEntities, stats.Civ);
        var queue = cm.QueryInterface<ProductionQueue>(trainer)!;

        var list = queue.GetTrainableEntities(cm);
        Assert.Equal(new[] { "units/athen/support_civilian_house" }, list.ToArray());
    }

    [Fact]
    public void EnqueueTraining_RejectsTemplateOutsideList()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var stats = templates.ExtractStats("structures/athen/civil_centre");
        var cm = BuildWorld(templates, ownerCiv: "athen", out var trainer,
            stats.TrainableEntities, stats.Civ);
        var queue = cm.QueryInterface<ProductionQueue>(trainer)!;

        // 列表外模板(旧的硬编码斯巴达兵)必须被拒——这正是本修复防的回归。
        Assert.False(queue.EnqueueTraining("units/spart/infantry_spearman_b", 1, cm));
        Assert.Equal("not-trainable", queue.LastRejectionReason);
        Assert.Equal(0, queue.QueueCount);

        // 列表内模板通过。
        Assert.True(queue.EnqueueTraining("units/athen/support_civilian", 1, cm));
        Assert.Equal(1, queue.QueueCount);
    }

    [Fact]
    public void EnqueueTraining_NoTokens_KeepsLegacyUngated()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        // 旧装配路径(SpawnBuilding 兜底,无 tokens):不门,保持旧行为。
        var cm = BuildWorld(templates, ownerCiv: "athen", out var trainer, "", "");
        var queue = cm.QueryInterface<ProductionQueue>(trainer)!;
        Assert.True(queue.EnqueueTraining("units/spart/support_civilian", 1, cm));
    }

    [Fact]
    public void Serialize_RoundTrip_PreservesTokensAndNativeCiv()
    {
        var queue = new ProductionQueue
        {
            TrainableTokens = "units/{native}/support_civilian units/{civ}/infantry_spearman_b",
            NativeCiv = "athen",
        };
        using var ms = new System.IO.MemoryStream();
        queue.Serialize(new ZeroAD.Sim.Serialization.BinarySerializer(new System.IO.BinaryWriter(ms)));
        ms.Position = 0;
        var clone = new ProductionQueue();
        clone.Deserialize(new ZeroAD.Sim.Serialization.BinaryDeserializer(new System.IO.BinaryReader(ms)));
        Assert.Equal(queue.TrainableTokens, clone.TrainableTokens);
        Assert.Equal("athen", clone.NativeCiv);
    }
}

using System;
using System.IO;
using System.Linq;
using Xunit;
using ZeroAD.Sim.Tutorial;

namespace ZeroAD.Sim.Tests;

public sealed class TutorialLevelLoaderTests
{
    private static string RepoDir(string relative)
    {
        var p = RepoPaths.Resolve(relative);
        Assert.True(p != null, $"repo marker not found: {relative}");
        return p!;
    }

    private static string TutorialDir() => RepoDir("godot/data/tutorials");

    [Fact]
    public void Loads_IntroductoryTutorial()
    {
        var level = TutorialLevelLoader.Load(
            Path.Combine(TutorialDir(), "introductory_tutorial.json"));
        Assert.Equal(27, level.Goals.Count);
        Assert.All(level.Goals, g => Assert.NotEmpty(g.Instructions));
    }

    [Fact]
    public void Loads_EconomyWalkthrough()
    {
        var level = TutorialLevelLoader.Load(
            Path.Combine(TutorialDir(), "starting_economy_walkthrough.json"));
        Assert.Equal(32, level.Goals.Count);
        Assert.All(level.Goals, g => Assert.NotEmpty(g.Instructions));
    }

    [Fact]
    public void Assembles_AllBindings()
    {
        foreach (var file in Directory.GetFiles(TutorialDir(), "*.json"))
        {
            var level = TutorialLevelLoader.Load(file);
            var goals = TutorialLevelLoader.Assemble(level);
            Assert.NotEmpty(goals);
            // 每个声明了绑定的目标都必须装配出对应事件处理器;无绑定目标是
            // Ready 按钮/计时器翻页(Delay<=0 手动,>0 计时)。
            for (int i = 0; i < goals.Count; i++)
            {
                var g = goals[i];
                bool hasHandler = g.OnPlayerCommand != null || g.OnTrainingQueued != null
                    || g.OnTrainingFinished != null || g.OnStructureBuilt != null
                    || g.OnResearchQueued != null || g.OnResearchFinished != null
                    || g.OnOwnershipChanged != null;
                Assert.Equal(level.Goals[i].On.Count > 0, hasHandler);
            }
        }
    }

    [Fact]
    public void Assembles_RequireIntoIsDone()
    {
        var level = TutorialLevelLoader.Load(
            Path.Combine(TutorialDir(), "introductory_tutorial.json"));
        var goals = TutorialLevelLoader.Assemble(level);
        // 双旗目标(农田+训练)与科技目标必须装配出 IsDone。
        var compound = level.Goals
            .Select((g, i) => (g, i))
            .Where(x => x.g.Require != null)
            .Select(x => x.i)
            .ToList();
        Assert.NotEmpty(compound);
        foreach (var i in compound)
            Assert.NotNull(goals[i].IsDone);
    }

    [Fact]
    public void Rejects_UnknownEventType()
    {
        var ex = Assert.Throws<InvalidDataException>(() => TutorialLevelLoader.Assemble(
            TutorialLevelLoader.Parse("""{"Map":"m","Goals":[{"Instructions":["x"],"On":[{"Event":"bogus"}]}]}""")));
        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public void Rejects_UnknownAction()
    {
        var ex = Assert.Throws<InvalidDataException>(() => TutorialLevelLoader.Assemble(
            TutorialLevelLoader.Parse("""{"Map":"m","Goals":[{"Instructions":["x"],"Actions":["bogus"]}]}""")));
        Assert.Contains("bogus", ex.Message);
    }
}

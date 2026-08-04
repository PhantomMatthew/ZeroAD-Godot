using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ZeroAD.Sim.Content
{
    /// <summary>
    /// skirmish/ 占位模板的文明替换——原版 components/SkirmishReplacer.js 的移植。
    /// skirmish 地图的实体使用文明无关占位模板（skirmish/units/default_*、
    /// skirmish/structures/default_*，是真实模板文件），开局时（原版 InitGame.js 广播
    /// MT_SkirmishReplace → 每实体 ReplaceEntities）按属主玩家文明替换为实际模板：
    ///   1. simulation/data/civs/{civ}.json 的 SkirmishReplacements 表有显式映射 → 用映射；
    ///   2. 否则用占位模板 SkirmishReplacer/general 元素的兜底模板；
    ///   3. 两者都没有 → 销毁该实体（如 special_starting_unit 仅 5 个文明有映射）；
    ///   4. 属主为 gaia → 销毁；查不到属主文明 → 保留占位（原版 if (!civ) return）。
    /// 结果模板名中的 {civ} 替换为文明代码。位置/朝向/属主不变——本类只做模板名决策，
    /// 实体的销毁/重建由调用方（SimBridge 在生成前改写实体定义表）天然获得同一语义。
    /// 替换时机等价于原版"世界构建完成、首回合开始前"，因此本过程不含任何随机/浮点，
    /// 对锁步确定性无影响。
    /// </summary>
    public sealed class SkirmishReplacer
    {
        private readonly TemplateLoader? _templates;
        private readonly string? _civsRoot;
        private readonly Dictionary<string, Dictionary<string, string>> _civCache =
            new(StringComparer.Ordinal);

        /// <param name="templates">模板加载器（general 兜底走继承合并后的 ParamNode，
        /// 对应原版 this.template.general）。null = 无 general 兜底。</param>
        /// <param name="civsRoot">simulation/data/civs 目录；null/不存在 = 全部 civ 表为空。</param>
        public SkirmishReplacer(TemplateLoader? templates, string? civsRoot)
        {
            _templates = templates;
            _civsRoot = string.IsNullOrEmpty(civsRoot) || !Directory.Exists(civsRoot) ? null : civsRoot;
        }

        /// <summary>由 templates 根推导 civs 目录（simulation/templates → simulation/data/civs）。
        /// 目录不存在返回 null。</summary>
        public static string? CivsRootFromTemplatesRoot(string? templatesRoot)
        {
            if (string.IsNullOrEmpty(templatesRoot)) return null;
            var candidate = Path.GetFullPath(Path.Combine(templatesRoot, "..", "data", "civs"));
            return Directory.Exists(candidate) ? candidate : null;
        }

        /// <summary>判定单个占位模板的替换结果（对应 ReplaceEntities 的模板名决策部分）。
        /// 返回 null = 销毁该实体；否则返回 {civ} 已代入的实际模板名。</summary>
        public string? ResolveReplacement(string templateName, string? civ)
        {
            // 原版：if (!templateName || civ == "gaia") DestroyEntity。空 civ 在此视为无效输入，
            // 与 gaia 同处理；属主完全查不到文明由 Apply 的 civForPlayer 返回 null 表达（保留）。
            if (string.IsNullOrEmpty(civ) || civ == "gaia") return null;

            string? replacement = null;
            if (LoadCivReplacements(civ).TryGetValue(templateName, out var mapped))
                replacement = mapped;
            else
                replacement = ReadGeneralFallback(templateName);

            if (string.IsNullOrEmpty(replacement)) return null;
            return replacement.Replace("{civ}", civ);
        }

        /// <summary>对场景实体列表原地执行替换（对应 InitGame 广播 MT_SkirmishReplace）。
        /// civForPlayer(playerId) 返回该玩家文明代码；返回 null = 保留占位实体不动（原版
        /// if (!civ) return——Atlas 里未分配玩家的实体推迟到首回合再处理，本移植保留占位）。
        /// 返回 (替换数, 销毁数) 供日志。</summary>
        public (int Replaced, int Destroyed) Apply(
            List<ScenarioEntityDef> entities, Func<int, string?> civForPlayer)
        {
            int replaced = 0, destroyed = 0;
            for (int i = entities.Count - 1; i >= 0; i--)
            {
                var def = entities[i];
                if (!def.Template.StartsWith("skirmish/", StringComparison.Ordinal)) continue;

                string? civ = civForPlayer(def.Player);
                if (civ == null) continue;

                string? resolved = ResolveReplacement(def.Template, civ);
                if (resolved == null)
                {
                    entities.RemoveAt(i);
                    destroyed++;
                }
                else
                {
                    def.Template = resolved;
                    replaced++;
                }
            }
            return (replaced, destroyed);
        }

        /// <summary>读取 simulation/data/civs/{civ}.json 的 SkirmishReplacements 表（按 civ 缓存）。
        /// 文件缺失/解析失败 → 空表（全部走 general 兜底），与原版读不到表时落 general 一致。</summary>
        private Dictionary<string, string> LoadCivReplacements(string civ)
        {
            if (_civCache.TryGetValue(civ, out var cached)) return cached;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_civsRoot != null)
            {
                string path = Path.Combine(_civsRoot, civ + ".json");
                if (File.Exists(path))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        if (doc.RootElement.TryGetProperty("SkirmishReplacements", out var table) &&
                            table.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in table.EnumerateObject())
                                map[prop.Name] = prop.Value.GetString() ?? "";
                        }
                    }
                    catch (Exception)
                    {
                        // 解析失败按空表处理
                    }
                }
            }
            _civCache[civ] = map;
            return map;
        }

        /// <summary>占位模板 SkirmishReplacer/general 兜底模板名（经父模板继承合并后的
        /// ParamNode，对应原版 this.template.general）。模板无该元素/加载失败 → null。</summary>
        private string? ReadGeneralFallback(string templateName)
        {
            if (_templates == null) return null;
            try
            {
                var node = _templates.LoadTemplate(templateName);
                var general = node.GetChild("SkirmishReplacer").GetChild("general");
                if (!general.IsOk) return null;
                var value = general.ToString();
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

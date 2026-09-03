using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ZeroAD.Sim.Content.Schema;

/// <summary>RelaxNG 子集校验器——原版 RelaxNG.cpp(libxml2 xmlRelaxNGValidateDoc)的移植。
/// 两阶段:静默结构匹配(名称/基数/属性/数据类型,memo 化的可结束位置集合)
/// 决定接受与否;接受失败再走诊断路径产出人类可读错误(元素路径 + 原因)。
/// interleave 用按名分划(语料中分支名类两两不相交;单个 anyName 通配分支兜余)。</summary>
public sealed class RngValidator
{
    private readonly RngGrammar _grammar;
    /// <summary>名类缓存:模式对象不可变且跨作用域共享,NamesOf 结果直接挂 validator。</summary>
    private readonly Dictionary<RngPattern, (HashSet<string> Names, bool Wildcard, bool HasText)> _namesOfMemo = new();

    public RngValidator(RngGrammar grammar) => _grammar = grammar;

    internal (HashSet<string> Names, bool Wildcard, bool HasText) NamesOfCached(RngPattern p)
    {
        if (_namesOfMemo.TryGetValue(p, out var cached)) return cached;
        var computed = ContentScope.NamesOfStatic(this, p, new HashSet<RngPattern>());
        _namesOfMemo[p] = computed;
        return computed;
    }

    /// <summary>校验一棵实例树(通常是模板合并根的合成根)。返回错误列表(空 = 通过)。</summary>
    public List<string> Validate(XmlInstanceNode root, int maxErrors = 30)
    {
        var errors = new List<string>();
        if (_grammar.Start is not RngElement startEl)
        {
            errors.Add("grammar start is not an element pattern");
            return errors;
        }
        if (!RngValidator.NameMatches(startEl.NameClass, root.Name))
        {
            errors.Add($"/{root.Name}: root element not allowed by grammar");
            return errors;
        }
        new ContentScope(this, errors, maxErrors).MatchContent(startEl.Content, root, "/" + root.Name);
        return errors;
    }

    internal static bool NameMatches(RngNameClass nc, string name) => nc switch
    {
        RngAnyName => true,
        RngNamedName n => n.Name == name,
        _ => false,
    };

    private static string DescribeName(RngNameClass nc) => nc switch
    {
        RngAnyName => "(any)",
        RngNamedName n => $"'{n.Name}'",
        _ => "(unknown)",
    };

    /// <summary>单个元素内容的匹配作用域(items = 子元素 + 尾部文本项;Ends memo)。</summary>
    private sealed class ContentScope
    {
        private readonly RngValidator _v;
        private readonly List<string> _errors;
        private readonly int _maxErrors;
        private readonly Dictionary<(RngPattern, int), HashSet<int>> _memo = new();
        private IReadOnlyList<XmlInstanceNode> _items = Array.Empty<XmlInstanceNode>();
        private string _textItem = "";      // 尾部文本(空 = 无文本项)
        private bool _hasText;

        public ContentScope(RngValidator v, List<string> errors, int maxErrors)
        {
            _v = v; _errors = errors; _maxErrors = maxErrors;
        }

        /// <summary>复用作用域:换一组 items 重算(无文本项)。</summary>
        public void Reset(IReadOnlyList<XmlInstanceNode> items)
        {
            _items = items;
            _hasText = false;
            _textItem = "";
            _memo.Clear();
        }

        private bool Full => _errors.Count >= _maxErrors;

        // ── 入口:元素内容(属性 + 子序列)──

        public void MatchContent(RngPattern content, XmlInstanceNode node, string path)
        {
            MatchAttributes(content, node, path);

            _items = node.Children;
            _textItem = node.Text?.Trim() ?? "";
            _hasText = _textItem.Length > 0;
            _memo.Clear();

            var childPattern = StripAttributes(content);
            int itemCount = _items.Count + (_hasText ? 1 : 0);
            var ends = Ends(childPattern, 0);
            if (ends.Contains(itemCount))
            {
                // 结构可行 → 引导全流程(属性/数据类型等内容错误在此精确报告)。
                GuidedSeq(childPattern, 0, itemCount, path);
                return;
            }

            // 失败诊断:先引导匹配最长可行前缀(暴露前缀内的数据类型错误),
            // 再报告首个卡住的位置。
            int reach = ends.Count == 0 ? 0 : ends.Max();
            if (reach > 0)
                GuidedSeq(childPattern, 0, reach, path);
            if (reach < itemCount)
            {
                if (reach == _items.Count && _hasText)
                {
                    // 卡在文本项:给出文本专属诊断(类型/枚举值)。
                    TextDiagnostic(childPattern, _textItem, path);
                }
                else
                {
                    Error($"{path}: element '{_items[reach].Name}' not allowed here");
                }
            }
            else
            {
                Error($"{path}: incomplete content — expected {DescribeExpected(childPattern)}");
            }
        }

        // ── 属性 ──

        private void MatchAttributes(RngPattern content, XmlInstanceNode node, string path)
        {
            var attrPatterns = new List<(RngAttribute Attr, bool Required)>();
            CollectAttributes(content, true, attrPatterns, new HashSet<RngPattern>());

            var matchedRequired = new HashSet<RngAttribute>();
            foreach (var (name, value) in node.Attributes)
            {
                bool anyNameMatch = false;
                bool ok = false;
                foreach (var (ap, required) in attrPatterns)
                {
                    if (!NameMatches(ap.NameClass, name)) continue;
                    anyNameMatch = true;
                    if (MatchTextSilent(ap.Content, value))
                    {
                        ok = true;
                        if (required) matchedRequired.Add(ap);
                        // 不 break:可选同名模式也算接受。
                    }
                }
                if (!ok)
                    Error(anyNameMatch
                        ? $"{path}: attribute '{name}' has invalid value '{value}'"
                        : $"{path}: attribute '{name}' not allowed here");
            }
            foreach (var (ap, required) in attrPatterns)
            {
                if (!required || matchedRequired.Contains(ap)) continue;
                if (ap.NameClass is RngNamedName req)
                    Error($"{path}: missing required attribute '{req.Name}'");
                else if (node.Attributes.Count == 0)
                    Error($"{path}: missing required attribute");
            }
        }

        private void CollectAttributes(RngPattern p, bool required,
            List<(RngAttribute, bool)> sink, HashSet<RngPattern> visited)
        {
            if (!visited.Add(p)) return;
            switch (p)
            {
                case RngAttribute a: sink.Add((a, required)); break;
                case RngGroup g: foreach (var c in g.Items) CollectAttributes(c, required, sink, visited); break;
                case RngInterleave i: foreach (var c in i.Items) CollectAttributes(c, required, sink, visited); break;
                case RngChoice c: foreach (var o in c.Options) CollectAttributes(o, false, sink, visited); break;
                case RngOptional o: CollectAttributes(o.Inner, false, sink, visited); break;
                case RngZeroOrMore z: CollectAttributes(z.Inner, false, sink, visited); break;
                case RngOneOrMore o: CollectAttributes(o.Inner, false, sink, visited); break;
                case RngRef r: CollectAttributes(_v._grammar.Resolve(r), required, sink, visited); break;
            }
        }

        /// <summary>属性模式从子序列模式中剔除(属性不参与子元素位置匹配)。</summary>
        private RngPattern StripAttributes(RngPattern p) => p switch
        {
            RngAttribute => new RngEmpty(),
            RngGroup g => new RngGroup(g.Items.Select(StripAttributes).ToList()),
            RngInterleave i => new RngInterleave(i.Items.Select(StripAttributes).ToList()),
            RngChoice c => new RngChoice(c.Options.Select(StripAttributes).ToList()),
            RngOptional o => o with { Inner = StripAttributes(o.Inner) },
            RngZeroOrMore z => z with { Inner = StripAttributes(z.Inner) },
            RngOneOrMore o => o with { Inner = StripAttributes(o.Inner) },
            RngList l => l with { Inner = StripAttributes(l.Inner) },
            _ => p,
        };

        // ── 静默位置集(接受性判定的核心)──

        /// <summary>模式从 pos 开始匹配的全部可能结束位置。</summary>
        private HashSet<int> Ends(RngPattern p, int pos)
        {
            var key = (p, pos);
            if (_memo.TryGetValue(key, out var cached)) return cached;
            // 先占位防左递归(ref 环;正常 grammar 无,防御性)。
            var result = new HashSet<int>();
            _memo[key] = result;
            ComputeEnds(p, pos, result);
            return result;
        }

        private int ItemCount => _items.Count + (_hasText ? 1 : 0);

        private bool IsTextItem(int idx) => _hasText && idx == _items.Count;

        private void ComputeEnds(RngPattern p, int pos, HashSet<int> result)
        {
            switch (p)
            {
                case RngEmpty:
                case RngAttribute:   // 已剔除;防御
                    result.Add(pos);
                    break;
                case RngNotAllowed:
                    break;
                case RngText:
                case RngData:
                case RngValue:
                case RngList:
                    // RelaxNG:text 匹配空内容(<Civ/> 对 <text/> 合法——上游大量
                    // 用空元素清空继承值,如 template_formation 的 <Civ/>)。
                    // 无文本项时视为空串再判一遍(text 过、decimal 拒)。
                    if (pos < ItemCount && IsTextItem(pos))
                    {
                        if (MatchTextSilent(p, _textItem))
                            result.Add(pos + 1);
                    }
                    else if (MatchTextSilent(p, ""))
                    {
                        result.Add(pos);
                    }
                    break;
                case RngElement el:
                    // 名称命中即消费位置(结构与内容判定分离):
                    // 内容问题留给引导阶段在该元素自己的作用域里精确报告,
                    // 不向上冒泡成误导性的 "element not allowed"。
                    if (pos < _items.Count && NameMatches(el.NameClass, _items[pos].Name))
                        result.Add(pos + 1);
                    break;
                case RngRef r:
                    result.UnionWith(Ends(_v._grammar.Resolve(r), pos));
                    break;
                case RngChoice c:
                    foreach (var o in c.Options) result.UnionWith(Ends(o, pos));
                    break;
                case RngOptional o:
                    result.Add(pos);
                    result.UnionWith(Ends(o.Inner, pos));
                    break;
                case RngGroup g:
                {
                    var current = new HashSet<int> { pos };
                    foreach (var sub in g.Items)
                    {
                        var next = new HashSet<int>();
                        foreach (int e in current) next.UnionWith(Ends(sub, e));
                        current = next;
                        if (current.Count == 0) break;
                    }
                    result.UnionWith(current);
                    break;
                }
                case RngZeroOrMore z:
                {
                    result.Add(pos);
                    var work = new Queue<int>();
                    work.Enqueue(pos);
                    while (work.Count > 0)
                    {
                        int e = work.Dequeue();
                        foreach (int e2 in Ends(z.Inner, e))
                        {
                            if (e2 > e && result.Add(e2)) work.Enqueue(e2);
                        }
                    }
                    break;
                }
                case RngOneOrMore o:
                {
                    var zom = new RngZeroOrMore(o.Inner);
                    foreach (int e in Ends(o.Inner, pos))
                        result.UnionWith(Ends(zom, e));
                    break;
                }
                case RngInterleave inter:
                {
                    // interleave 在序列中间时可消费任意前缀:逐终点尝试按名分划。
                    for (int e = pos; e <= ItemCount; e++)
                    {
                        if (TryPartitionInterleave(inter.Items, pos, e, silent: true, out _, path: null))
                            result.Add(e);
                        // 首个不可分配项之后不可能再有可行前缀,提前终止。
                        if (e < ItemCount && !CanAssignAnywhere(inter.Items, e))
                            break;
                    }
                    break;
                }
            }
        }

        // ── interleave 按名分划 ──

        private (HashSet<string> Names, bool Wildcard, bool HasText) NamesOf(RngPattern p)
            => _v.NamesOfCached(p);

        internal static (HashSet<string>, bool, bool) NamesOfStatic(RngValidator v, RngPattern p, HashSet<RngPattern> visited)
        {
            if (!visited.Add(p)) return (new HashSet<string>(), false, false);
            switch (p)
            {
                case RngElement el:
                    return el.NameClass is RngNamedName n
                        ? (new HashSet<string> { n.Name }, false, false)
                        : (new HashSet<string>(), true, false);
                case RngChoice c:
                {
                    var names = new HashSet<string>();
                    bool wild = false, text = false;
                    foreach (var o in c.Options)
                    {
                        var (nm, w, t) = NamesOfStatic(v, o, visited);
                        names.UnionWith(nm); wild |= w; text |= t;
                    }
                    return (names, wild, text);
                }
                case RngGroup g:
                case RngInterleave i:
                {
                    var items = p is RngGroup g2 ? g2.Items : ((RngInterleave)p).Items;
                    var names = new HashSet<string>();
                    bool wild = false, text = false;
                    foreach (var o in items)
                    {
                        var (nm, w, t) = NamesOfStatic(v, o, visited);
                        names.UnionWith(nm); wild |= w; text |= t;
                    }
                    return (names, wild, text);
                }
                case RngOptional o: return NamesOfStatic(v, o.Inner, visited);
                case RngZeroOrMore z: return NamesOfStatic(v, z.Inner, visited);
                case RngOneOrMore o: return NamesOfStatic(v, o.Inner, visited);
                case RngList l: return NamesOfStatic(v, l.Inner, visited);
                case RngRef r: return NamesOfStatic(v, v._grammar.Resolve(r), visited);
                case RngText: case RngData: case RngValue: return (new HashSet<string>(), false, true);
                default: return (new HashSet<string>(), false, false);
            }
        }

        private bool CanAssignAnywhere(IReadOnlyList<RngPattern> branches, int itemIdx)
        {
            if (IsTextItem(itemIdx))
                return branches.Any(b => NamesOf(b).HasText);
            string name = _items[itemIdx].Name;
            foreach (var b in branches)
            {
                var (names, wild, _) = NamesOf(b);
                if (wild || names.Contains(name)) return true;
            }
            return false;
        }

        /// <summary>把 items[pos..end) 分配到各分支。成功 → assignment(每分支的子序列)。
        /// 同名多分支(歧义)取声明序首个;语料无此情形(sweep 测试把关)。</summary>
        private bool TryPartitionInterleave(IReadOnlyList<RngPattern> branches, int pos, int end,
            bool silent, out List<XmlInstanceNode>[] assignment, string? path)
        {
            var classes = branches.Select(NamesOf).ToList();
            assignment = new List<XmlInstanceNode>[branches.Count];
            for (int i = 0; i < branches.Count; i++) assignment[i] = new List<XmlInstanceNode>();

            int wildcardIdx = -1;
            for (int i = 0; i < classes.Count; i++)
                if (classes[i].Wildcard) { wildcardIdx = i; break; }

            bool hasTextItem = false;
            for (int j = pos; j < end; j++)
            {
                if (IsTextItem(j))
                {
                    // 文本不进元素分划;在后面统一对 HasText 分支校验。
                    // (组件 define 的 interleave 包装会把文本模式顶成独立分支,
                    // 如 Auras = interleave[attribute→empty, text]。)
                    hasTextItem = true;
                    continue;
                }
                string name = _items[j].Name;
                int idx = -1;
                for (int i = 0; i < classes.Count; i++)
                {
                    if (classes[i].Names.Contains(name)) { idx = i; break; }
                }
                if (idx < 0) idx = wildcardIdx;
                if (idx < 0)
                {
                    if (!silent && path != null) Error($"{path}: element '{name}' not allowed here");
                    return false;
                }
                assignment[idx].Add(_items[j]);
            }

            // 文本项:须被某个 HasText 分支接受。
            if (hasTextItem)
            {
                int ti = classes.FindIndex(c => c.HasText);
                if (ti < 0)
                {
                    if (!silent && path != null) Error($"{path}: text not allowed here");
                    return false;
                }
                if (!MatchTextSilent(branches[ti], _textItem))
                {
                    if (!silent && path != null) TextDiagnostic(branches[ti], _textItem, path);
                    return false;
                }
            }

            // 各分支校验自己的元素子序列(保序)。分支作用域复用(热路径:全语料
            // 数十万级调用,每分支 new 一个作用域曾是主要分配源)。
            var scope = new ContentScope(_v, _errors, _maxErrors);
            for (int i = 0; i < branches.Count; i++)
            {
                // 纯文本分支(无元素分配):其文本部分已由上面的文本校验满足。
                if (assignment[i].Count == 0 && classes[i].HasText && hasTextItem)
                    continue;
                scope.Reset(assignment[i]);
                if (!scope.Ends(branches[i], 0).Contains(assignment[i].Count))
                {
                    if (!silent && path != null)
                    {
                        // 诊断:在分支子序列上走完整内容匹配拿具体错误。
                        scope.GuidedSeq(branches[i], 0,
                            scope.Ends(branches[i], 0).Count == 0 ? 0 : scope.Ends(branches[i], 0).Max(), path);
                        if (assignment[i].Count > 0)
                            Error($"{path}: interleave branch {i} did not fully match " +
                                $"(last assigned element '{assignment[i][^1].Name}')");
                    }
                    return false;
                }
                if (!silent)
                {
                    // 静默已过 → 引导一遍以校验内容(数据类型/属性)并收集错误。
                    scope.GuidedSeq(branches[i], 0, assignment[i].Count, path ?? "");
                }
            }
            return true;
        }

        // ── 引导匹配(静默已接受后收集内容错误;或失败诊断)──

        private void GuidedSeq(RngPattern p, int pos, int end, string path)
        {
            if (Full) return;
            switch (p)
            {
                case RngEmpty:
                case RngAttribute:
                    return;
                case RngText:
                case RngData:
                case RngValue:
                case RngList:
                    if (pos < ItemCount && IsTextItem(pos))
                        MatchText(p, _textItem, path);
                    return;
                case RngElement el:
                    if (pos >= _items.Count || !NameMatches(el.NameClass, _items[pos].Name))
                    {
                        Error($"{path}: expected element {DescribeName(el.NameClass)}");
                        return;
                    }
                    // 嵌套元素内容必须进新作用域——MatchContent 会重置 items/memo,
                    // 复用本作用域会把父序列的状态抹掉。
                    new ContentScope(_v, _errors, _maxErrors)
                        .MatchContent(el.Content, _items[pos], path + "/" + _items[pos].Name);
                    return;
                case RngRef r:
                    GuidedSeq(_v._grammar.Resolve(r), pos, end, path);
                    return;
                case RngNotAllowed:
                    Error($"{path}: content not allowed");
                    return;
                case RngChoice c:
                    foreach (var o in c.Options)
                    {
                        if (Ends(o, pos).Contains(end)) { GuidedSeq(o, pos, end, path); return; }
                    }
                    Error($"{path}: no alternative matched");
                    return;
                case RngOptional o:
                    if (end == pos) return;
                    GuidedSeq(o.Inner, pos, end, path);
                    return;
                case RngGroup g:
                {
                    // 后缀组预计算,保证 Ends memo 命中同一对象。
                    var suffix = new RngPattern[g.Items.Count + 1];
                    suffix[g.Items.Count] = new RngEmpty();
                    for (int i = g.Items.Count - 1; i >= 0; i--)
                        suffix[i] = g.Items.Count - 1 - i == 0
                            ? g.Items[i]
                            : new RngGroup(g.Items.Skip(i).ToList());
                    int cur = pos;
                    for (int i = 0; i < g.Items.Count; i++)
                    {
                        var ends = Ends(g.Items[i], cur);
                        int chosen = -1;
                        foreach (int e in ends.OrderBy(x => x))
                        {
                            if (e > end) continue;
                            if (i == g.Items.Count - 1 ? e == end : Ends(suffix[i + 1], e).Contains(end))
                            {
                                chosen = e;
                                break;
                            }
                        }
                        if (chosen < 0)
                        {
                            Error($"{path}: could not match required content at item {cur}");
                            return;
                        }
                        GuidedSeq(g.Items[i], cur, chosen, path);
                        cur = chosen;
                    }
                    return;
                }
                case RngZeroOrMore z:
                {
                    int cur = pos;
                    while (cur < end && !Full)
                    {
                        var ends = Ends(z.Inner, cur);
                        int chosen = -1;
                        foreach (int e in ends.OrderBy(x => x))
                        {
                            if (e <= cur || e > end) continue;
                            if (Ends(z, e).Contains(end)) { chosen = e; break; }
                        }
                        if (chosen < 0)
                        {
                            Error($"{path}: repeated content stopped matching at item {cur}");
                            return;
                        }
                        GuidedSeq(z.Inner, cur, chosen, path);
                        cur = chosen;
                    }
                    return;
                }
                case RngOneOrMore o:
                {
                    // 首选非空首轮(e > pos);inner 可空(如 interleave-of-optionals)时
                    // 空首轮合法但无用,仅在没有非空候选时兜底,剩余交给 zeroOrMore 段。
                    var ends = Ends(o.Inner, pos);
                    int chosen = -1;
                    foreach (int e in ends.OrderBy(x => x))
                    {
                        if (e <= pos || e > end) continue;
                        if (Ends(new RngZeroOrMore(o.Inner), e).Contains(end)) { chosen = e; break; }
                    }
                    if (chosen < 0 && end == pos && ends.Contains(pos))
                        chosen = pos;   // 整体零项:可空 inner 的空匹配
                    if (chosen < 0)
                    {
                        Error($"{path}: required repeated content did not match");
                        return;
                    }
                    if (chosen > pos)
                        GuidedSeq(o.Inner, pos, chosen, path);
                    if (chosen < end)
                        GuidedSeq(new RngZeroOrMore(o.Inner), chosen, end, path);
                    return;
                }
                case RngInterleave inter:
                    TryPartitionInterleave(inter.Items, pos, end, silent: false, out _, path);
                    return;
            }
        }

        // ── 文本匹配 ──

        private bool MatchTextSilent(RngPattern p, string text)
        {
            switch (p)
            {
                case RngText: return true;
                case RngEmpty: return text.Length == 0;
                case RngData d: return CheckData(d, text);
                case RngValue val: return string.Equals(val.Value, text, StringComparison.Ordinal);
                case RngChoice c: return c.Options.Any(o => MatchTextSilent(o, text));
                case RngOptional o: return text.Length == 0 || MatchTextSilent(o.Inner, text);
                case RngZeroOrMore z: return text.Length == 0 || MatchTextSilent(z.Inner, text);
                case RngOneOrMore o: return MatchTextSilent(o.Inner, text);
                case RngGroup g: return g.Items.All(i => MatchTextSilent(i, text));
                case RngInterleave i: return i.Items.All(x => MatchTextSilent(x, text));
                case RngRef r: return MatchTextSilent(_v._grammar.Resolve(r), text);
                case RngList l:
                    foreach (string tok in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                        if (!MatchTextSilent(l.Inner, tok)) return false;
                    return true;
                default: return false;
            }
        }

        private void MatchText(RngPattern p, string text, string path)
        {
            if (MatchTextSilent(p, text)) return;
            TextDiagnostic(p, text, path);
        }

        private void TextDiagnostic(RngPattern p, string text, string path)
        {
            // 给最具体的一层诊断。
            switch (p)
            {
                case RngChoice c:
                    foreach (var o in c.Options)
                        if (MatchTextSilent(o, text)) return;
                    var values = new List<string>();
                    CollectValues(p, values, new HashSet<RngPattern>());
                    Error(values.Count > 0
                        ? $"{path}: '{text}' is not one of: {string.Join(", ", values)}"
                        : $"{path}: '{text}' does not match any alternative");
                    return;
                case RngRef r:
                    TextDiagnostic(_v._grammar.Resolve(r), text, path);
                    return;
                case RngOptional o: TextDiagnostic(o.Inner, text, path); return;
                case RngZeroOrMore z: TextDiagnostic(z.Inner, text, path); return;
                case RngOneOrMore o: TextDiagnostic(o.Inner, text, path); return;
                default:
                    Error($"{path}: '{text}' — {DescribeTextPattern(p)}");
                    return;
            }
        }

        private void CollectValues(RngPattern p, List<string> sink, HashSet<RngPattern> visited)
        {
            if (!visited.Add(p) || sink.Count > 12) return;
            switch (p)
            {
                case RngValue v: sink.Add(v.Value); break;
                case RngChoice c: foreach (var o in c.Options) CollectValues(o, sink, visited); break;
                case RngRef r: CollectValues(_v._grammar.Resolve(r), sink, visited); break;
            }
        }

        private static string DescribeTextPattern(RngPattern p) => p switch
        {
            RngData d => $"value is not a valid {d.Type}{DescribeParams(d)}",
            RngValue v => $"expected '{v.Value}'",
            RngText => "unexpected mismatch",   // text 接受一切,到不了这
            RngList l => "token list item invalid: " + DescribeTextPattern(l.Inner),
            _ => "does not match required pattern",
        };

        private static string DescribeParams(RngData d)
        {
            if (d.Params.Count == 0) return "";
            return " (" + string.Join(", ", d.Params.Select(kv => kv.Key + " " + kv.Value)) + ")";
        }

        private static bool CheckData(RngData d, string text)
        {
            bool numericOk;
            double num = 0;
            switch (d.Type)
            {
                case "boolean":
                    return text is "true" or "false" or "1" or "0";
                case "decimal":
                case "float":
                case "double":
                    numericOk = double.TryParse(text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out num);
                    break;
                case "integer":
                case "long":
                    numericOk = long.TryParse(text, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out long l);
                    num = l;
                    break;
                case "nonNegativeInteger":
                    numericOk = long.TryParse(text, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out long nl) && nl >= 0;
                    num = numericOk ? nl : 0;
                    break;
                case "positiveInteger":
                    numericOk = long.TryParse(text, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out long pl) && pl > 0;
                    num = numericOk ? pl : 0;
                    break;
                case "string":
                    numericOk = true;
                    break;
                default:
                    // 未知数据类型:宽松接受(与 libxml2 的 string 兜底一致)。
                    numericOk = true;
                    break;
            }
            if (!numericOk) return false;
            foreach (var (pname, pval) in d.Params)
            {
                if (!double.TryParse(pval, NumberStyles.Float, CultureInfo.InvariantCulture, out double bound))
                    continue;
                switch (pname)
                {
                    case "minInclusive": if (num < bound) return false; break;
                    case "maxInclusive": if (num > bound) return false; break;
                    case "minExclusive": if (num <= bound) return false; break;
                    case "maxExclusive": if (num >= bound) return false; break;
                }
            }
            return true;
        }

        // ── 工具 ──

        private string DescribeExpected(RngPattern p)
        {
            var (names, wild, text) = NamesOf(p);
            if (wild) return "(any element)";
            if (names.Count > 0) return "one of: " + string.Join(", ", names.Take(10));
            if (text) return "text";
            return "(nothing)";
        }

        private void Error(string message)
        {
            if (!Full) _errors.Add(message);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ZeroAD.Sim.Content.Schema;

/// <summary>从组件 JS 源提取 RelaxNG schema 片段——原版 CComponentManager 读
/// X.prototype.Schema 属性(注册时由 JS 引擎求值)的移植。不求值整个 JS:
/// 只解释 schema 表达式实际用到的语法(语料全量盘点):
///  "..." / '...' / `...` 字符串字面量(含转义),+ 拼接,// 与 /* *\/ 注释,
///  Resources.BuildSchema / Resources.BuildChoicesSchema /
///  RequirementsHelper.BuildSchema / AttackHelper.BuildAttackEffectsSchema 调用,
///  同文件 X.prototype.Y 字符串属性引用与无参方法调用(如 Resistance 的
///  BuildResistanceSchema)。未识别构造抛 ExtractException(调用方记 Diag 并跳过该组件)。
/// 组件名取 Engine.RegisterComponentType(IID_*, "Name", ...) 的注册名(如
///  MotionBall.js → "MotionBallScripted"),缺失时回退文件名。</summary>
public static class ComponentSchemaExtractor
{
    public sealed class ExtractException(string message) : Exception(message);

    public sealed record Result(string ComponentName, string Schema);

    /// <summary>提取组件名 + 求值后的 schema 字符串;无 Schema 属性 → null
    /// (上游默认 &lt;empty/&gt;,由 grammar 组合层补)。</summary>
    public static Result? Extract(string fileName, string jsSource,
        SchemaHelpers.ResourceSchemaData? resources = null)
    {
        var extractor = new Extractor(jsSource, resources ?? SchemaHelpers.ResourceSchemaData.Default);
        string? schema = extractor.ExtractSchema();
        if (schema == null) return null;
        string name = extractor.ExtractRegisteredName() ?? fileName;
        return new Result(name, schema);
    }

    private sealed class Extractor
    {
        private readonly string _src;
        private readonly SchemaHelpers.ResourceSchemaData _res;
        private readonly Dictionary<string, string> _propBodies = new(StringComparer.Ordinal);
        private readonly HashSet<string> _evaluating = new();

        public Extractor(string src, SchemaHelpers.ResourceSchemaData res)
        {
            _src = src;
            _res = res;
            // 同文件 prototype 属性/方法体登记(惰性求值)。
            foreach (Match m in Regex.Matches(_src,
                @"(\w+)\.prototype\.(\w+)\s*="))
            {
                string key = m.Groups[1].Value + "." + m.Groups[2].Value;
                _propBodies.TryAdd(key, _src[(m.Index + m.Length)..]);
            }
        }

        public string? ExtractRegisteredName()
        {
            var m = Regex.Match(_src, @"RegisterComponentType\([^,]+,\s*""([^""]+)""");
            return m.Success ? m.Groups[1].Value : null;
        }

        public string? ExtractSchema()
        {
            // 只认 "Schema"(大小写敏感;prototype.Schema 是上游约定)。
            var m = Regex.Match(_src, @"(\w+)\.prototype\.Schema\s*=");
            if (!m.Success) return null;
            string key = m.Groups[1].Value + ".Schema";
            return EvalProperty(key);
        }

        /// <summary>求值 prototype 属性;方法(function(){ return &lt;expr&gt;; })取 return 表达式。</summary>
        private string EvalProperty(string key)
        {
            if (!_propBodies.TryGetValue(key, out string? body) || body == null)
                throw new ExtractException($"unknown reference '{key}'");
            if (!_evaluating.Add(key))
                throw new ExtractException($"recursive schema reference '{key}'");
            try
            {
                var tz = new Tokenizer(body);
                tz.SkipFunctionPrelude();   // function() { return 前缀(如有)
                string value = ParseConcat(tz);
                return value;
            }
            finally { _evaluating.Remove(key); }
        }

        // ── 表达式求值:term (+ term)* ──

        private string ParseConcat(Tokenizer tz)
        {
            var sb = new StringBuilder();
            sb.Append(ParseTerm(tz));
            while (tz.Peek().Kind == TokKind.Plus)
            {
                tz.Next();
                sb.Append(ParseTerm(tz));
            }
            return sb.ToString();
        }

        private string ParseTerm(Tokenizer tz)
        {
            var tok = tz.Next();
            switch (tok.Kind)
            {
                case TokKind.Str:
                    return tok.Text;
                case TokKind.Ident:
                {
                    string ident = tok.Text;
                    if (tz.Peek().Kind == TokKind.LParen)
                        return EvalCall(ident, tz);
                    // 属性引用:Attack.prototype.preferredClassesSchema → 同文件登记键 Attack.preferredClassesSchema
                    if (ident.StartsWith("this.", StringComparison.Ordinal))
                        ident = ident[5..];   // this.X(罕见,防御)
                    string key = ident.Replace(".prototype.", ".", StringComparison.Ordinal);
                    if (_propBodies.ContainsKey(key))
                        return EvalProperty(key);
                    throw new ExtractException($"unsupported identifier '{ident}' in schema expression");
                }
                default:
                    throw new ExtractException($"unexpected token {tok.Kind} in schema expression");
            }
        }

        private string EvalCall(string callee, Tokenizer tz)
        {
            var args = ParseArgs(tz);
            switch (callee)
            {
                case "Resources.BuildSchema":
                {
                    string datatype = args.Count > 0 ? args[0] ?? "" : "";
                    string? additionalJoined = args.Count > 1 ? args[1] : null;
                    var additional = additionalJoined != null
                        ? additionalJoined.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        : Array.Empty<string>();
                    bool subtypes = args.Count > 2 && args[2] == "true";
                    return SchemaHelpers.ResourcesBuildSchema(datatype, additional, subtypes, _res);
                }
                case "Resources.BuildChoicesSchema":
                    return SchemaHelpers.ResourcesBuildChoicesSchema(
                        args.Count > 0 && args[0] == "true", _res);
                case "RequirementsHelper.BuildSchema":
                    return SchemaHelpers.RequirementsBuildSchema();
                case "AttackHelper.BuildAttackEffectsSchema":
                    return SchemaHelpers.AttackEffectsBuildSchema();
                default:
                {
                    // 同文件方法:Resistance.prototype.BuildResistanceSchema()
                    string key = callee.Replace(".prototype.", ".", StringComparison.Ordinal);
                    if (_propBodies.ContainsKey(key))
                        return EvalProperty(key);
                    throw new ExtractException($"unsupported call '{callee}' in schema expression");
                }
            }
        }

        /// <summary>参数列表:字符串字面量 / true|false / 字符串数组字面量。
        /// 数组以 "\n" 连接编码(如 ["xp"] → "xp";调用方拆分)。空数组 → null 哨兵。</summary>
        private List<string?> ParseArgs(Tokenizer tz)
        {
            var args = new List<string?>();
            tz.Expect(TokKind.LParen);
            if (tz.Peek().Kind == TokKind.RParen) { tz.Next(); return args; }
            while (true)
            {
                var tok = tz.Next();
                switch (tok.Kind)
                {
                    case TokKind.Str: args.Add(tok.Text); break;
                    case TokKind.Ident when tok.Text == "true": args.Add("true"); break;
                    case TokKind.Ident when tok.Text == "false": args.Add("false"); break;
                    case TokKind.LBracket:
                    {
                        var items = new List<string>();
                        while (tz.Peek().Kind != TokKind.RBracket)
                        {
                            var it = tz.Next();
                            if (it.Kind != TokKind.Str)
                                throw new ExtractException("non-string array item in schema call");
                            items.Add(it.Text);
                            if (tz.Peek().Kind == TokKind.Comma) tz.Next();
                        }
                        tz.Next();   // ]
                        args.Add(items.Count == 0 ? null : string.Join('\n', items));
                        break;
                    }
                    default:
                        throw new ExtractException($"unsupported argument token {tok.Kind}");
                }
                if (tz.Peek().Kind == TokKind.Comma) { tz.Next(); continue; }
                break;
            }
            tz.Expect(TokKind.RParen);
            return args;
        }
    }

    // ── 词法 ──

    private enum TokKind { Str, Ident, Plus, LParen, RParen, LBracket, RBracket, Comma, End }

    private readonly struct Token(TokKind kind, string text)
    {
        public TokKind Kind { get; } = kind;
        public string Text { get; } = text;
    }

    /// <summary>tokenizer:字符串(双引号/单引号/反引号模板串,含转义)、
    /// 点分标识符、运算符;空白与 // 、/* *\/ 注释跳过。</summary>
    private sealed class Tokenizer
    {
        private readonly string _s;
        private int _i;
        private Token? _peeked;

        public Tokenizer(string s) => _s = s;

        public Token Peek()
        {
            _peeked ??= Scan();
            return _peeked.Value;
        }

        public Token Next()
        {
            var t = Peek();
            _peeked = null;
            return t;
        }

        public void Expect(TokKind kind)
        {
            var t = Next();
            if (t.Kind != kind)
                throw new ExtractException($"expected {kind}, got {t.Kind}");
        }

        /// <summary>方法体前缀:function(...) { return(求值 return 后的表达式)。</summary>
        public void SkipFunctionPrelude()
        {
            SkipTrivia();
            if (!_s.AsSpan(_i).StartsWith("function", StringComparison.Ordinal)) return;
            int ret = _s.IndexOf("return", _i, StringComparison.Ordinal);
            if (ret < 0) throw new ExtractException("function without return in schema property");
            _i = ret + "return".Length;
            _peeked = null;
        }

        private void SkipTrivia()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (char.IsWhiteSpace(c)) { _i++; continue; }
                if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')
                {
                    while (_i < _s.Length && _s[_i] != '\n') _i++;
                    continue;
                }
                if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '*')
                {
                    int close = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    _i = close < 0 ? _s.Length : close + 2;
                    continue;
                }
                break;
            }
        }

        private Token Scan()
        {
            SkipTrivia();
            if (_i >= _s.Length) return new Token(TokKind.End, "");
            char c = _s[_i];
            switch (c)
            {
                case '+': _i++; return new Token(TokKind.Plus, "+");
                case '(': _i++; return new Token(TokKind.LParen, "(");
                case ')': _i++; return new Token(TokKind.RParen, ")");
                case '[': _i++; return new Token(TokKind.LBracket, "[");
                case ']': _i++; return new Token(TokKind.RBracket, "]");
                case ',': _i++; return new Token(TokKind.Comma, ",");
                case ';': _i++; return new Token(TokKind.End, ";");
                case '"' or '\'' or '`': return ScanString(c);
                default:
                    if (char.IsLetter(c) || c == '_' || c == '$')
                    {
                        int start = _i;
                        while (_i < _s.Length &&
                               (char.IsLetterOrDigit(_s[_i]) || _s[_i] is '_' or '$' or '.'))
                            _i++;
                        return new Token(TokKind.Ident, _s[start.._i]);
                    }
                    throw new ExtractException($"unexpected character '{c}' in schema expression");
            }
        }

        private Token ScanString(char quote)
        {
            _i++;   // 开引号
            var sb = new StringBuilder();
            while (_i < _s.Length)
            {
                char c = _s[_i++];
                if (c == quote) return new Token(TokKind.Str, sb.ToString());
                if (c == '\\' && _i < _s.Length)
                {
                    char e = _s[_i++];
                    sb.Append(e switch
                    {
                        'n' => '\n', 't' => '\t', 'r' => '\r',
                        '\\' => '\\', '"' => '"', '\'' => '\'', '`' => '`',
                        '0' => '\0',
                        _ => e,   // 其余转义原样保留(如 \&)
                    });
                    continue;
                }
                if (quote == '`' && c == '$' && _i < _s.Length && _s[_i] == '{')
                    throw new ExtractException("template literal interpolation unsupported");
                sb.Append(c);
            }
            throw new ExtractException("unterminated string literal in schema expression");
        }
    }
}

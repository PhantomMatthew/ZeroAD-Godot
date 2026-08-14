using System;
using System.Collections.Generic;

namespace ZeroAD.Sim
{
    /// <summary>日志级别。</summary>
    public enum DiagLevel { Log, Warn, Err }

    /// <summary>一条日志记录。tag 用于按通道过滤;Level 决定 Godot 侧输出到 Print/
    /// PushWarning/PrintErr。</summary>
    public readonly record struct DiagEntry(DateTime TimeUtc, string Tag, DiagLevel Level, string Message);

    /// <summary>
    /// 统一日志通道(诊断方案 3)。收拢此前散落的 GD.Print/GD.PrintErr/GD.PushWarning
    /// 与内核 Console.WriteLine。内核零 Godot 依赖:本类只持一个可注入的 sink,Godot
    /// 层启动时把 sink 接到 GD.Print;未注入时默认写 Console(供 headless/测试用)。
    ///
    /// tag 过滤:按通道静音/放行,与具体级别无关。
    ///   ZEROAD_LOG="replay,lockstep"  只显示这两个 tag
    ///   ZEROAD_LOG="-replay"          静音 replay tag(其余照常)
    ///   ZEROAD_LOG 未设/空            全显示
    /// 运行时可经 DiagPanel(in-game 面板)调 Mute/Unmute/SetAllowOnly 动态改。
    /// </summary>
    public static class Diag
    {
        private static readonly object _lock = new();
        private static readonly HashSet<string> _muted = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _allowOnly = new(StringComparer.OrdinalIgnoreCase);

        static Diag()
        {
            ApplyEnvVar();
        }

        /// <summary>输出目标。Godot 层 DiagGodot.Install() 把它接到 GD.Print 系列;
        /// 默认 sink 写 Console(headless / 测试)。线程安全。</summary>
        public static Action<DiagEntry> Sink { get; set; } = DefaultSink;

        /// <summary>诊断面板订阅(内存环形缓冲写入走这里,与 Sink 并行)。</summary>
        public static event Action<DiagEntry>? EntryLogged;

        public static void Log(string tag, string message) => Emit(tag, DiagLevel.Log, message);
        public static void Warn(string tag, string message) => Emit(tag, DiagLevel.Warn, message);
        public static void Err(string tag, string message) => Emit(tag, DiagLevel.Err, message);

        private static void Emit(string tag, DiagLevel level, string message)
        {
            lock (_lock)
            {
                if (_muted.Contains(tag)) return;
                if (_allowOnly.Count > 0 && !_allowOnly.Contains(tag)) return;
            }
            var entry = new DiagEntry(DateTime.UtcNow, tag, level, message);
            try { Sink(entry); } catch { /* sink 失败不炸 sim */ }
            try { EntryLogged?.Invoke(entry); } catch { }
        }

        // ── tag 过滤(面板/环境变量都走这里)──

        public static void Mute(string tag) { lock (_lock) _muted.Add(tag); }
        public static void Unmute(string tag) { lock (_lock) _muted.Remove(tag); }
        public static bool IsMuted(string tag) { lock (_lock) return _muted.Contains(tag); }

        /// <summary>只放行这些 tag(空 = 全放行)。面板"只看这些"用。</summary>
        public static void SetAllowOnly(IEnumerable<string>? tags)
        {
            lock (_lock)
            {
                _allowOnly.Clear();
                if (tags != null)
                    foreach (var t in tags) _allowOnly.Add(t);
            }
        }

        /// <summary>清空放行集合(= 全显示)。</summary>
        public static void ClearAllowOnly() { lock (_lock) _allowOnly.Clear(); }

        private static void ApplyEnvVar()
        {
            string raw = Environment.GetEnvironmentVariable("ZEROAD_LOG") ?? "";
            if (raw.Length == 0) return;
            var only = new List<string>();
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.StartsWith("-", StringComparison.Ordinal))
                    Mute(part.Substring(1));
                else
                    only.Add(part);
            }
            if (only.Count > 0) SetAllowOnly(only);
        }

        private static void DefaultSink(DiagEntry e)
        {
            string prefix = e.Level switch
            {
                DiagLevel.Warn => "WARN",
                DiagLevel.Err => "ERR ",
                _ => "    ",
            };
            System.Console.WriteLine($"{prefix} [{e.Tag}] {e.Message}");
        }
    }
}

using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;

namespace ZeroAD.Godot.Actors;

public static class ActorDiagnostics
{
	private static readonly HashSet<string> _seen = new();
	private static readonly object _lock = new();
	private static readonly Dictionary<string, int> _counts = new();
	private static readonly Dictionary<string, int> _success = new();
	private static string? _logPath;
	private static bool _truncated;

	private static string LogPath
	{
		get
		{
			_logPath ??= Path.Combine(ProjectSettings.GlobalizePath("res://"), "actor_diag.txt");
			return _logPath;
		}
	}

	public static void Fallback(string template, string reason)
	{
		string key = template + "|" + reason;
		lock (_lock)
		{
			_counts.TryGetValue(key, out int n);
			_counts[key] = n + 1;
			if (!_seen.Add(key))
				return;
		}
		GD.PushWarning($"[ActorDiag] BOX '{template}' — {reason}");
		WriteLine($"BOX\t{template}\t{reason}");
	}

	public static void Resolved(string template, string actorPath)
	{
		lock (_lock)
		{
			_success.TryGetValue(template, out int n);
			_success[template] = n + 1;
		}
	}

	public static void DumpSummary()
	{
		var sb = new StringBuilder();
		sb.AppendLine("=== ActorDiag summary ===");
		sb.AppendLine($"resolved-ok templates: {_success.Count}, box-fallback templates: {_counts.Count}");
		sb.AppendLine("--- box fallbacks (template | reason | count) ---");
		lock (_lock)
		{
			foreach (var kv in _counts)
				sb.AppendLine($"x{kv.Value}\t{kv.Key}");
		}
		string text = sb.ToString();
		GD.Print(text);
		WriteLine(text);
	}

	private static void WriteLine(string line)
	{
		try
		{
			lock (_lock)
			{
				if (!_truncated)
				{
					File.Delete(LogPath);
					_truncated = true;
				}
				File.AppendAllText(LogPath, line + "\n");
			}
		}
		catch { }
	}
}

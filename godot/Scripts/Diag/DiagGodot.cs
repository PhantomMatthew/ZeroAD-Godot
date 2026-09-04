using Godot;
using ZeroAD.Sim;

namespace ZeroAD.Godot.Diagnostics
{
	/// <summary>Diag 的 Godot 接线:启动时把内核 Diag.Sink 接到 Godot 输出
	/// (log→GD.Print, warn→GD.PushWarning, err→GD.PrintErr),同时把每条日志写进
	/// 内存环形缓冲,供 in-game DiagPanel 读取;并全量落盘 user://logs/zeroad.log
	/// (每次启动截断重写——"进不去图"类问题的事后诊断依据,加载异常只进控制台
	/// 的时代用户没有任何可查的凭据)。
	///
	/// 调用时机:Main._Ready / MainMenu 启动早期(越早越好,接住 startup 日志)。
	/// 幂等:重复 Install 不重复挂。</summary>
	public static class DiagGodot
	{
		private const int BufferCapacity = 500;
		private static readonly DiagEntry[] _ring = new DiagEntry[BufferCapacity];
		private static int _head;      // 下一条写入位置
		private static int _count;     // 已写条数(<= Capacity)
		private static bool _installed;
		private static readonly object _lock = new();
		private static string? _logPath;

		/// <summary>安装 sink + 缓冲写入 + 落盘。幂等。</summary>
		public static void Install()
		{
			if (_installed) return;
			_installed = true;
			InitLogFile();
			ZeroAD.Sim.Diag.Sink = WriteGodot;
			ZeroAD.Sim.Diag.EntryLogged += BufferWrite;
			ZeroAD.Sim.Diag.EntryLogged += WriteFile;
		}

		/// <summary>user://logs/zeroad.log,每次启动截断(只留当前会话,免轮转)。
		/// 文件系统任何失败都静默降级为不落盘——日志绝不能打挂游戏。</summary>
		private static void InitLogFile()
		{
			try
			{
				string dir = ProjectSettings.GlobalizePath("user://logs");
				System.IO.Directory.CreateDirectory(dir);
				_logPath = System.IO.Path.Combine(dir, "zeroad.log");
				System.IO.File.WriteAllText(_logPath,
					$"=== ZeroAD-Godot session {System.DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
			}
			catch (System.Exception)
			{
				_logPath = null;
			}
		}

		private static void WriteFile(DiagEntry e)
		{
			string? p = _logPath;
			if (p == null) return;
			try
			{
				System.IO.File.AppendAllText(p,
					$"{e.TimeUtc.ToLocalTime():HH:mm:ss.fff} {e.Level,-5} [{e.Tag}] {e.Message}\n");
			}
			catch (System.Exception)
			{
				// 磁盘满/权限等:静默丢弃,不打断游戏。
			}
		}

		private static void WriteGodot(DiagEntry e)
		{
			string text = $"[{e.Tag}] {e.Message}";
			switch (e.Level)
			{
				case DiagLevel.Warn: GD.PushWarning(text); break;
				case DiagLevel.Err: GD.PrintErr(text); break;
				default: GD.Print(text); break;
			}
		}

		private static void BufferWrite(DiagEntry e)
		{
			lock (_lock)
			{
				_ring[_head] = e;
				_head = (_head + 1) % BufferCapacity;
				if (_count < BufferCapacity) _count++;
			}
		}

		/// <summary>最近 N 条日志(新→旧),供面板渲染。</summary>
		public static DiagEntry[] Recent(int max = 100)
		{
			lock (_lock)
			{
				int n = System.Math.Min(max, _count);
				var outArr = new DiagEntry[n];
				for (int i = 0; i < n; i++)
				{
					int idx = (_head - 1 - i + BufferCapacity) % BufferCapacity;
					outArr[i] = _ring[idx];
				}
				return outArr;
			}
		}
	}
}

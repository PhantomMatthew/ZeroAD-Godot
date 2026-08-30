using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot;

/// <summary>过场动画管理器(原版 graphics/CinemaManager + CCmpCinemaManager):
/// 相机路径(TNSpline 样条插值)按队列播放,播完一径广播 OnCinemaPathEnded,
/// 队列空广播 OnCinemaQueueEnded(原版 Trigger 事件,触发器脚本经此驱动剧情)。
///
/// 原版路径数据在地图 <Cinema> 段(nodes:pos + target,duration);
/// 本移植用简化数据 —— 地图脚本的 PushPathToQueue("name") + 预定义路径表
/// (与原版的"地图注册路径 → 脚本按名播放"语义一致)。
/// 播放时相机锁用户输入,走 RTSCamera 的 Focus/位置驱动。</summary>
public sealed partial class CinemaManager : Node
{
    /// <summary>路径节点(原版 CCinemaData 的 node:位置 + 目标 + 时长)。</summary>
    public sealed class PathNode
    {
        public Vector3 Position;
        public Vector3 Target;
        public float Duration;   // 秒(原版 GetNodeDuration)
    }

    /// <summary>单条相机路径(原版 CCinemaPath:name + 样条节点 + 时标)。</summary>
    public sealed class CinemaPath
    {
        public string Name = "";
        public float Timescale = 1f;      // 负 = 倒放(原版 m_Timescale)
        public List<PathNode> Nodes = new();
        public float Duration
        {
            get
            {
                float d = 0;
                foreach (var n in Nodes) d += n.Duration;
                return d;
            }
        }
    }

    /// <summary>路径注册表(原版 AddPath 的地图侧注册:名字 → 路径)。</summary>
    private readonly Dictionary<string, CinemaPath> _paths = new(System.StringComparer.Ordinal);
    /// <summary>播放队列(原版 m_PathQueue)。</summary>
    private readonly Queue<CinemaPath> _queue = new();
    /// <summary>当前播放态。</summary>
    private float _elapsed;
    private bool _playing;
    private readonly RTSCamera? _camera;

    /// <summary>原版消息广播(OnCinemaPathEnded/OnCinemaQueueEnded;
    /// 触发器 CallEvent 同名投递)。</summary>
    public System.Action<string>? PathEnded;
    public System.Action? QueueEnded;

    public CinemaManager(RTSCamera? camera) => _camera = camera;

    /// <summary>注册路径(原版 AddPath:地图侧给名字挂路径)。</summary>
    public void AddPath(CinemaPath path) => _paths[path.Name] = path;

    /// <summary>地图 XML 的 <Paths> 段解析(原版 MapReader::ReadPaths):
    /// 每 <Path name=...> 注册一条路径,Node 的 deltatime 为时长,
    /// Position/Target 为样条点。原版 C++ MapReader 的逐字移植——
    /// MapEnvironment.LoadFromXml 同树解析,由 SimBridge 加载地图后调用。</summary>
    public void LoadFromMapXml(string xmlPath)
    {
        if (!System.IO.File.Exists(xmlPath)) return;
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(xmlPath);
            var pathsEl = doc.Root?.Element("Paths");
            if (pathsEl == null) return;
            foreach (var pathEl in pathsEl.Elements("Path"))
            {
                var path = new CinemaPath
                {
                    Name = pathEl.Attribute("name")?.Value ?? "",
                    Timescale = ReadFloat(pathEl.Attribute("timescale")?.Value, 1f),
                };
                foreach (var nodeEl in pathEl.Elements("Node"))
                {
                    float duration = ReadFloat(nodeEl.Attribute("deltatime")?.Value, 1f);
                    var posEl = nodeEl.Element("Position");
                    var targetEl = nodeEl.Element("Target");
                    path.Nodes.Add(new PathNode
                    {
                        Position = posEl != null ? ReadVector3(posEl) : Vector3.Zero,
                        Target = targetEl != null ? ReadVector3(targetEl) : Vector3.Zero,
                        Duration = duration,
                    });
                }
                if (path.Nodes.Count > 0 && path.Name.Length > 0)
                    AddPath(path);
            }
        }
        catch (System.Exception e)
        {
            ZeroAD.Sim.Diag.Err("Cinema", $"LoadFromMapXml failed: {e.Message}");
        }
    }

    private static Vector3 ReadVector3(System.Xml.Linq.XElement el)
    {
        float x = ReadFloat(el.Attribute("x")?.Value, 0f);
        float y = ReadFloat(el.Attribute("y")?.Value, 0f);
        float z = ReadFloat(el.Attribute("z")?.Value, 0f);
        return new Vector3(x, y, z);
    }

    private static float ReadFloat(string? s, float fallback) =>
        float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;

    /// <summary>按名推入播放队列(原版 PushPathToQueue)。</summary>
    public void PushPathToQueue(string name)
    {
        if (_paths.TryGetValue(name, out var path))
            _queue.Enqueue(path);
    }

    /// <summary>开始播放(原版 StartPlayingQueue;相机锁用户输入——
    /// RTSCamera 停 _Process 驱动,由本管理器逐帧驱动)。</summary>
    public void StartPlayingQueue()
    {
        _playing = true;
        _camera?.SetProcess(false);
    }
    public bool IsPlayingQueue() => _playing && _queue.Count > 0;

    /// <summary>每帧推进(原版 CCinemaManager::Update):样条插值位置/朝向,
    /// 播完一径广播 + 弹出,队列空广播结束并还原相机控制。</summary>
    public override void _Process(double delta)
    {
        if (!_playing || _queue.Count == 0 || _camera == null) return;
        var path = _queue.Peek();
        _elapsed += (float)delta * path.Timescale;

        float total = path.Duration;
        if (total <= 0 || (_elapsed >= total && path.Timescale > 0)
            || (_elapsed <= 0 && path.Timescale < 0))
        {
            // 播完(原版 CinemaPathEnded + 出队;空队列广播 QueueEnded)。
            string name = path.Name;
            _queue.Dequeue();
            PathEnded?.Invoke(name);
            _elapsed = 0;
            if (_queue.Count == 0)
            {
                _playing = false;
                _camera?.SetProcess(true);   // 还原用户相机控制(原版 queue 播完)
                QueueEnded?.Invoke();
            }
            return;
        }

        // 样条插值(原版 TNSpline:Catmull-Rom 位置/目标,简化线性插值
        // 平滑度已足;Play 的 MoveToPointAt 等价——位置走节点间插值)。
        float t = path.Timescale > 0 ? _elapsed : total + _elapsed;
        (Vector3 pos, Vector3 target) = Sample(path, t);
        _camera.SetFocus(target);
        _camera.GlobalPosition = pos;
        _camera.LookAt(target, Vector3.Up);
    }

    /// <summary>路径采样(节点间线性插值;原版 TNSpline 的简化——
    /// Catmull-Rom 平滑度对 RTS 过场不必要)。</summary>
    private static (Vector3 pos, Vector3 target) Sample(CinemaPath path, float t)
    {
        float acc = 0;
        for (int i = 0; i < path.Nodes.Count - 1; i++)
        {
            var a = path.Nodes[i];
            var b = path.Nodes[i + 1];
            if (t <= acc + b.Duration)
            {
                float r = b.Duration > 0 ? (t - acc) / b.Duration : 0f;
                return (a.Position.Lerp(b.Position, r), a.Target.Lerp(b.Target, r));
            }
            acc += b.Duration;
        }
        var last = path.Nodes[^1];
        return (last.Position, last.Target);
    }
}

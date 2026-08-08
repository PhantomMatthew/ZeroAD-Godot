using Godot;

namespace ZeroAD.Godot;

public sealed partial class RTSCamera : Camera3D
{
    public Vector3? Focus => _focus;
    public float Yaw => _yaw;

    private float _yaw = 0f;
    private float _pitch = -0.7f;
    private float _distance = 120f;
    private Vector3 _focus = new(274f, 27f, 113f);
    // Last seen mouse position. When the mouse hasn't been moved yet, Godot reports
    // (0,0) — including via synthetic MouseMotion events on window creation/focus.
    // Edge-pan is only enabled once we observe a real position change (different from
    // the previous frame), so the camera doesn't drift before the user touches the mouse.
    private Vector2 _lastMousePos = new(-1, -1);
    private bool _edgePanEnabled;

    private const float DefaultFov = 45f;

    private const float PanSpeed = 120f;
    private const float RotateSpeed = 2.0f;
    private const float EdgeMargin = 15f;
    private const float MinDistance = 5f;
    private const float MaxDistance = 200f;
    private const float MinPitch = -1.3f;
    private const float MaxPitch = -0.15f;

    public override void _Ready()
    {
        Fov = DefaultFov;
        Near = 2f;
        Far = 4096f;
        // 阴影代理挂第 2 层(见 ShadowProxyManager):相机剔除使其不可见,
        // 方向光投影掩码默认全含 → 不可见但照常写阴影贴图。
        CullMask &= ~ShadowProxyManager.ProxyLayer;
        UpdateTransform();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        bool moved = false;

        Vector3 camDir = new(Mathf.Sin(_yaw), 0f, Mathf.Cos(_yaw));
        // 平移在 sim 空间进行(_focus 是 sim 坐标):世界经 _worldRoot 镜像(visZ=S−simZ),
        // 视线方向 visDir=−camDir 换回 sim 得 forward=(−sin,0,+cos),屏幕右=东(+x)。
        Vector3 forward = new(-camDir.X, 0f, camDir.Z);
        Vector3 right = new(camDir.Z, 0f, camDir.X);

        if (Input.IsActionPressed("cam_up"))    { _focus += forward * PanSpeed * dt; moved = true; }
        if (Input.IsActionPressed("cam_down"))  { _focus -= forward * PanSpeed * dt; moved = true; }
        if (Input.IsActionPressed("cam_left"))  { _focus -= right * PanSpeed * dt; moved = true; }
        if (Input.IsActionPressed("cam_right")) { _focus += right * PanSpeed * dt; moved = true; }

        if (Input.IsActionPressed("cam_rotate_cw"))  { _yaw += RotateSpeed * dt; moved = true; }
        if (Input.IsActionPressed("cam_rotate_ccw")) { _yaw -= RotateSpeed * dt; moved = true; }

		if (Input.IsKeyPressed(Key.Equal) || Input.IsKeyPressed(Key.KpAdd))
		{ _distance = Mathf.Max(MinDistance, _distance * Mathf.Pow(0.5f, dt)); moved = true; }
		if (Input.IsKeyPressed(Key.Minus) || Input.IsKeyPressed(Key.KpSubtract))
		{ _distance = Mathf.Min(MaxDistance, _distance * Mathf.Pow(2.0f, dt)); moved = true; }

        var vp = GetViewport();
        if (vp != null)
        {
            var mp = vp.GetMousePosition();
            var sz = vp.GetVisibleRect().Size;
            // Detect the first REAL mouse movement: the position differs from the
            // previous frame. Synthetic events on window creation report (0,0) every
            // frame, so _lastMousePos stays (0,0) and the guard never opens. Once the
            // user actually moves the mouse, the position changes and edge-pan unlocks.
            if (mp != _lastMousePos)
            {
                if (_lastMousePos.X >= 0) // not the initial (-1,-1) sentinel
                    _edgePanEnabled = true;
                _lastMousePos = mp;
            }
            if (_edgePanEnabled)
            {
                if (mp.X < EdgeMargin)       { _focus -= right * PanSpeed * dt; moved = true; }
                else if (mp.X > sz.X - EdgeMargin) { _focus += right * PanSpeed * dt; moved = true; }
                if (mp.Y < EdgeMargin)       { _focus -= forward * PanSpeed * dt; moved = true; }
                else if (mp.Y > sz.Y - EdgeMargin) { _focus += forward * PanSpeed * dt; moved = true; }
            }
        }

        if (moved)
        {
            _focus.Y = TerrainHeightService.Sample(_focus.X, _focus.Z);
            UpdateTransform();
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed)
        {
			if (mb.ButtonIndex == MouseButton.WheelUp)
			{
				if (Input.IsKeyPressed(Key.Shift))
					_yaw += 0.3f;
				else
					_distance = Mathf.Max(MinDistance, _distance * 0.85f);
				UpdateTransform();
			}
			else if (mb.ButtonIndex == MouseButton.WheelDown)
			{
				if (Input.IsKeyPressed(Key.Shift))
					_yaw -= 0.3f;
				else
					_distance = Mathf.Min(MaxDistance, _distance * 1.15f);
				UpdateTransform();
			}
        }
    }

    public void SetFocus(Vector3 focus)
    {
        _focus = focus;
        _focus.Y = TerrainHeightService.Sample(_focus.X, _focus.Z);
        UpdateTransform();
    }

    /// <summary>按场景 XML 的作者机位恢复开局视角(原版 GameView 语义:Position +
    /// Rotation(航向,0=朝 +z 北) + Declination(俯角)三者直接决定画面;此前忽略
    /// 两个角度、从"机位→CC"向量反推,Arcadia 视角因此跑偏)。聚焦点 = 视线与地面
    /// 的交点;镜像世界(vis z = WorldSize − sim z)下世界航向 = −rotation,俯角取负
    /// (向下看)。输入为 sim 坐标。</summary>
    public void PlaceFromScenarioCamera(Vector3 camSimPos, float rotation, float declination)
    {
        float sinD = Mathf.Max(0.05f, Mathf.Sin(declination));
        float hd = Mathf.Cos(declination);
        var fwdSim = new Vector3(Mathf.Sin(rotation) * hd, -sinD, Mathf.Cos(rotation) * hd);
        // 先按平地估步长,再按地形采样修正一次(坡地图焦点更准)。
        float t = camSimPos.Y / sinD;
        float fx = camSimPos.X + fwdSim.X * t;
        float fz = camSimPos.Z + fwdSim.Z * t;
        float groundY = TerrainHeightService.Sample(fx, fz);
        t = Mathf.Max(10f, (camSimPos.Y - groundY) / sinD);
        fx = camSimPos.X + fwdSim.X * t;
        fz = camSimPos.Z + fwdSim.Z * t;

        _yaw = -rotation;
        _pitch = Mathf.Clamp(-declination, MinPitch, MaxPitch);
        // 视距不做 MaxDistance 上限钳(作者机位优先;首次缩放输入才拉回范围内)。
        _distance = Mathf.Max(MinDistance, t);
        SetFocus(new Vector3(fx, groundY, fz));  // SetFocus 内部重采地形 Y 并 UpdateTransform
    }

    /// <summary>_focus(sim)的视觉空间坐标:visZ = WorldSize − simZ(对齐 _worldRoot 镜像)。</summary>
    private Vector3 FocusVisual() =>
        new(_focus.X, _focus.Y, TerrainHeightService.MirrorZ(_focus.Z));

    private void UpdateTransform()
    {
        float hd = _distance * Mathf.Cos(_pitch);
        float vd = _distance * Mathf.Sin(-_pitch);
        Vector3 offset = new(hd * Mathf.Sin(_yaw), vd, hd * Mathf.Cos(_yaw));
        Vector3 focusVis = FocusVisual();
        GlobalPosition = focusVis + offset;
        LookAt(focusVis, Vector3.Up);
    }
}

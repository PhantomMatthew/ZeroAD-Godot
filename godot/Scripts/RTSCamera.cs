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

    /// <summary>Positions the orbit camera so its world position matches the scenario's
    /// authored &lt;Camera&gt; element, deriving yaw/pitch/distance from the delta between
    /// the camera position and the current focus (look-at). This is how 0 A.D. starts a
    /// scenario: the Atlas editor's last camera pose is baked into the XML, and the game
    /// restores it on launch. Subsequent user pan/rotate then update the orbit params
    /// normally. 输入是 sim 坐标;世界视觉经 _worldRoot 镜像,先把 eye 换到视觉空间再推姿态。</summary>
    public void PlaceFromScenarioCamera(Vector3 camWorldPos)
    {
        Vector3 camVis = new(camWorldPos.X, camWorldPos.Y, TerrainHeightService.MirrorZ(camWorldPos.Z));
        Vector3 delta = camVis - FocusVisual();
        float horizDist = Mathf.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
        // offset = (hd*sin(yaw), vd, hd*cos(yaw)) → yaw = atan2(delta.X, delta.Z)。
        // Sign matches because both offset.X and delta.X are world-space camera offsets
        // from focus along the same axes.
        _yaw = Mathf.Atan2(delta.X, delta.Z);
        _distance = Mathf.Sqrt(delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z);
        // offset.Y = distance * sin(-pitch); positive offset.Y (camera above focus) needs
        // negative pitch (looking down). Inverted atan2 to land on the right sign directly.
        _pitch = -Mathf.Atan2(delta.Y, horizDist);
        _pitch = Mathf.Clamp(_pitch, MinPitch, MaxPitch);
        UpdateTransform();
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

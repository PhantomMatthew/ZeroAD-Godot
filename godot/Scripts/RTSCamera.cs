using Godot;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

	public sealed partial class RTSCamera : Camera3D
	{
		public Vector3? Focus => _focus;
		public float Yaw => _yaw;

		/// <summary>跟随选中单位(原版 camera.follow 热键 setCameraFollow):
		/// 平滑逼近目标;任何滚轮/移动输入打断跟随(原版"Break out of following
		/// mode when the user starts scrolling");目标消失/不可见 → 停跟随。</summary>
		public EntityId? FollowTarget
		{
			get => _followTarget;
			set { _followTarget = value; _followActive = value.HasValue; }
		}
		private EntityId? _followTarget;
		private bool _followActive;

		/// <summary>平滑加减速(原版 CameraController 的 CSmoothedValue):
		/// 相机位置/缩放/旋转用目标值平滑逼近,停止时按 minDelta 截断——
		/// 消除输入突变导致的画面抖动(原版 SmoothedValue.Update 语义)。</summary>
		private sealed class SmoothedValue
		{
			private double _target;
			private double _current;
			private readonly float _smoothness;
			private readonly float _minDelta;

			public SmoothedValue(float initial, float smoothness = 0.5f, float minDelta = 0.0001f)
			{
				_target = initial;
				_current = initial;
				_smoothness = smoothness;
				_minDelta = minDelta;
			}

			public float Target => (float)_target;
			public float Current => (float)_current;

			public void AddSmoothly(float delta) => _target += delta;
			public void SetValueSmoothly(float v) => _target = v;
			public void SetValue(float v) { _target = v; _current = v; }

			/// <summary>原版 CSmoothedValue.Update:按 smoothness^10dt 的指数平滑逼近。</summary>
			public float Update(float dt)
			{
				double diff = _target - _current;
				if (Math.Abs(diff) < _minDelta) return 0f;
				double p = Math.Pow(_smoothness, 10.0 * dt);
				double delta = diff * (1.0 - p);
				_current += delta;
				return (float)delta;
			}
		}

		// 平滑字段(原版 m_PosX/Y/Z、 m_Zoom、 m_RotateX/Y 的 SmoothedValue)。
		private SmoothedValue _smFocusX;
		private SmoothedValue _smFocusZ;
		private SmoothedValue _smDistance;
		private SmoothedValue _smYaw;
		private SmoothedValue _smPitch;

	/// <summary>Free camera(自由飞行)模式:开启后 WASD 平移(垂直视角方向)、
	/// QE 升降、Shift 加速、滚轮调速度;RTS 边缘平移/缩放/地形吸附全停。
	/// 排查"场景里有什么不该有的东西"(遮挡/漂浮/错位)用。</summary>
	public bool FreeFlyEnabled
	{
		get => _freeFly;
		set
		{
			_freeFly = value;
			if (!value) UpdateTransform();   // 回 RTS 模式时重建正常机位
		}
	}
	private bool _freeFly;
	private float _flySpeed = 60f;   // 滚轮调(米/秒,Shift ×4)

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

		// 右键 pan 拖拽(原版 camera.pan:中键按下拖鼠标平移;view.drag.speed/
		// inverted 选项)。本移植:右键按住拖拽(原版中键在主选右键已占用,
		// 按用户操作习惯改右键;速度/反向走 OptionsApplier 读用户配置)。
		private bool _rightDragPanning;
		private Vector2 _dragLastPos;

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
			// 平滑字段初始化(原版 CameraController 的 SmoothedValue 默认值)。
			_smFocusX = new SmoothedValue(_focus.X);
			_smFocusZ = new SmoothedValue(_focus.Z);
			_smDistance = new SmoothedValue(_distance);
			_smYaw = new SmoothedValue(_yaw);
			_smPitch = new SmoothedValue(_pitch);
			UpdateTransform();
		}

	public override void _Process(double delta)
	{
		if (_freeFly)
		{
			FlyProcess((float)delta);
			return;
		}

		float dt = (float)delta;
		bool moved = false;

		Vector3 camDir = new(Mathf.Sin(_yaw), 0f, Mathf.Cos(_yaw));
		// 平移在 sim 空间进行(_focus 是 sim 坐标):世界经 _worldRoot 镜像(visZ=S−simZ),
		// 视线方向 visDir=−camDir 换回 sim 得 forward=(−sin,0,+cos),屏幕右=东(+x)。
		Vector3 forward = new(-camDir.X, 0f, camDir.Z);
		Vector3 right = new(camDir.Z, 0f, camDir.X);

		// 用户移动输入打断跟随(原版 CameraController:Break out of following mode
		// when the user starts scrolling)。
		bool userMoved = false;
		if (Input.IsActionPressed("cam_up"))    { _smFocusX.AddSmoothly(forward.X * PanSpeed * dt); _smFocusZ.AddSmoothly(forward.Z * PanSpeed * dt); moved = true; userMoved = true; }
		if (Input.IsActionPressed("cam_down"))  { _smFocusX.AddSmoothly(-forward.X * PanSpeed * dt); _smFocusZ.AddSmoothly(-forward.Z * PanSpeed * dt); moved = true; userMoved = true; }
		if (Input.IsActionPressed("cam_left"))  { _smFocusX.AddSmoothly(-right.X * PanSpeed * dt); _smFocusZ.AddSmoothly(-right.Z * PanSpeed * dt); moved = true; userMoved = true; }
		if (Input.IsActionPressed("cam_right")) { _smFocusX.AddSmoothly(right.X * PanSpeed * dt); _smFocusZ.AddSmoothly(right.Z * PanSpeed * dt); moved = true; userMoved = true; }

		if (Input.IsActionPressed("cam_rotate_cw"))  { _smYaw.AddSmoothly(RotateSpeed * dt); moved = true; }
		if (Input.IsActionPressed("cam_rotate_ccw")) { _smYaw.AddSmoothly(-RotateSpeed * dt); moved = true; }

		if (Input.IsKeyPressed(Key.Equal) || Input.IsKeyPressed(Key.KpAdd))
		{ _smDistance.AddSmoothly(-_smDistance.Target * 0.5f * dt); moved = true; userMoved = true; }
		if (Input.IsKeyPressed(Key.Minus) || Input.IsKeyPressed(Key.KpSubtract))
		{ _smDistance.AddSmoothly(_smDistance.Target * 1.0f * dt); moved = true; userMoved = true; }

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
				if (mp.X < EdgeMargin)       { _smFocusX.AddSmoothly(-right.X * PanSpeed * dt); _smFocusZ.AddSmoothly(-right.Z * PanSpeed * dt); moved = true; userMoved = true; }
				else if (mp.X > sz.X - EdgeMargin) { _smFocusX.AddSmoothly(right.X * PanSpeed * dt); _smFocusZ.AddSmoothly(right.Z * PanSpeed * dt); moved = true; userMoved = true; }
				// Godot 屏幕坐标 Y 向下为正:顶边 mp.Y 小 → 应前进(+= forward,对齐 cam_up);
				// 底边 mp.Y 大 → 应后退。此前两分支接反,鼠标上移反而后退。
				if (mp.Y < EdgeMargin)       { _smFocusX.AddSmoothly(forward.X * PanSpeed * dt); _smFocusZ.AddSmoothly(forward.Z * PanSpeed * dt); moved = true; userMoved = true; }
				else if (mp.Y > sz.Y - EdgeMargin) { _smFocusX.AddSmoothly(-forward.X * PanSpeed * dt); _smFocusZ.AddSmoothly(-forward.Z * PanSpeed * dt); moved = true; userMoved = true; }
			}
		}

		// 跟随选中单位(原版 m_FollowEntity:平滑逼近目标位置)。
		if (_followActive && _followTarget.HasValue)
		{
			var targetPos = GetEntityWorldPosition(_followTarget.Value);
			if (targetPos.HasValue)
			{
				var p = targetPos.Value;
				_smFocusX.AddSmoothly(p.X - _smFocusX.Target);
				_smFocusZ.AddSmoothly(p.Z - _smFocusZ.Target);
				moved = true;
			}
			else
			{
				// 目标消失/不可见 → 停跟随(原版 m_FollowEntity = INVALID_ENTITY)。
				_followActive = false;
				_followTarget = null;
			}
		}
		if (userMoved)
		{
			_followActive = false;
			_followTarget = null;
		}

		// 平滑更新(原版 Update(deltaRealTime):按 smoothness^10dt 指数逼近目标)。
		_focus.X = _smFocusX.Update(dt);
		_focus.Z = _smFocusZ.Update(dt);
		_distance = Mathf.Clamp(_smDistance.Update(dt), MinDistance, MaxDistance);
		_yaw = _smYaw.Update(dt);
		_pitch = Mathf.Clamp(_smPitch.Update(dt), MinPitch, MaxPitch);

		if (moved)
		{
			_focus.Y = TerrainHeightService.Sample(_focus.X, _focus.Z);
			UpdateTransform();
		}
	}

	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb)
		{
			// 右键 pan 拖拽(原版 camera.pan 中键语义):按下记起点,移动拖拽平移,
			// 松开结束;拖拽期间打断跟随(原版 Break out of following mode 同款)。
			if (mb.ButtonIndex == MouseButton.Right)
			{
				if (mb.Pressed)
				{
					_rightDragPanning = true;
					_dragLastPos = mb.Position;
					_followActive = false;
					_followTarget = null;
				}
				else if (_rightDragPanning)
				{
					_rightDragPanning = false;
				}
			}
			else if (mb.Pressed)
			{
				if (_freeFly)
				{
					// 自由模式滚轮 = 调飞行速度(不缩放视距)
					if (mb.ButtonIndex == MouseButton.WheelUp)
						_flySpeed = Mathf.Clamp(_flySpeed * 1.3f, 5f, 2000f);
					else if (mb.ButtonIndex == MouseButton.WheelDown)
						_flySpeed = Mathf.Clamp(_flySpeed / 1.3f, 5f, 2000f);
					return;
				}
				if (mb.ButtonIndex == MouseButton.WheelUp)
				{
					if (Input.IsKeyPressed(Key.Shift))
						_smYaw.AddSmoothly(0.3f);
					else
						_smDistance.SetValueSmoothly(Mathf.Max(MinDistance, _smDistance.Target * 0.85f));
					UpdateTransform();
				}
				else if (mb.ButtonIndex == MouseButton.WheelDown)
				{
					if (Input.IsKeyPressed(Key.Shift))
						_smYaw.AddSmoothly(-0.3f);
					else
						_smDistance.SetValueSmoothly(Mathf.Min(MaxDistance, _smDistance.Target * 1.15f));
					UpdateTransform();
				}
			}
		}
		else if (@event is InputEventMouseMotion motion && _rightDragPanning)
		{
			// 拖拽平移(原版 view.drag.speed × 像素位移;inverted 选项反向)。
			float speed = Options.OptionsApplier.GetFloat("view.drag.speed", 0.5f);
			bool inverted = Options.OptionsApplier.GetBool("view.drag.inverted", false);
			var delta = motion.Position - _dragLastPos;
			_dragLastPos = motion.Position;
			float dx = inverted ? -delta.X : delta.X;
			float dz = inverted ? delta.Y : -delta.Y;   // 屏幕 Y 向下为正:拖上前进
			Vector3 camDir = new(Mathf.Sin(_yaw), 0f, Mathf.Cos(_yaw));
			Vector3 right = new(camDir.Z, 0f, camDir.X);
			Vector3 forward = new(-camDir.X, 0f, camDir.Z);
			_smFocusX.AddSmoothly((right.X * dx + forward.X * dz) * speed);
			_smFocusZ.AddSmoothly((right.Z * dx + forward.Z * dz) * speed);
			_focus.Y = TerrainHeightService.Sample(_focus.X, _focus.Z);
			UpdateTransform();
		}
	}

    public void SetFocus(Vector3 focus)
    {
        _focus = focus;
        _focus.Y = TerrainHeightService.Sample(_focus.X, _focus.Z);
        UpdateTransform();
    }

	/// <summary>dev 截图钩子用：直接设镜头距离（夹在 Min/Max 内）。</summary>
	public void SetDistance(float distance)
	{
		_distance = Mathf.Clamp(distance, MinDistance, MaxDistance);
		UpdateTransform();
	}

	/// <summary>跟随目标的世界位置(原版 GetInterpolatedTransform 的简化——
	/// 直接读 PositionComponent 当前位;消失/驻军 → null 停跟随)。</summary>
	private Vector3? GetEntityWorldPosition(EntityId entity)
	{
		var sim = SimSystem.Sim;
		if (sim == null) return null;
		var pos = sim.QueryInterface<PositionComponent>(entity);
		if (pos == null || !pos.InWorld) return null;
		return new Vector3(pos.Position.X.ToFloat(), pos.Position.Y.ToFloat(),
			pos.Position.Z.ToFloat());
	}

    /// <summary>Free camera 逐帧移动:WASD 沿视线方向平移(垂直俯仰也参与),
    /// QE 垂直升降,Shift ×4 加速,滚轮调基准速度。绕开 RTS 的地形吸附/边缘平移。</summary>
    private void FlyProcess(float dt)
    {
        float speed = _flySpeed * (Input.IsKeyPressed(Key.Shift) ? 4f : 1f);
        Vector3 dir = Vector3.Zero;
        // 相机朝向的前/右/上(视觉空间)
        var fwd = -GlobalTransform.Basis.Z;   // 相机前方
        var right = GlobalTransform.Basis.X;
        var up = Vector3.Up;
        if (Input.IsKeyPressed(Key.W)) dir += fwd;
        if (Input.IsKeyPressed(Key.S)) dir -= fwd;
        if (Input.IsKeyPressed(Key.A)) dir -= right;
        if (Input.IsKeyPressed(Key.D)) dir += right;
        if (Input.IsKeyPressed(Key.Q)) dir -= up;
        if (Input.IsKeyPressed(Key.E)) dir += up;
        if (dir != Vector3.Zero)
            GlobalPosition += dir.Normalized() * speed * dt;
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

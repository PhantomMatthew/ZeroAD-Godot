using Godot;

namespace ZeroAD.Godot;

public sealed partial class RTSCamera : Camera3D
{
    private float _yaw = 0f;
    private float _pitch = -0.7f;
    private float _distance = 120f;
    private Vector3 _focus = new(274f, 27f, 113f);

    private const float PanSpeed = 120f;
    private const float ZoomSpeed = 256f;
    private const float RotateSpeed = 2.0f;
    private const float EdgeMargin = 15f;
    private const float MinDistance = 50f;
    private const float MaxDistance = 200f;
    private const float MinPitch = -1.2f;
    private const float MaxPitch = -0.45f;

    public override void _Ready() => UpdateTransform();

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        bool moved = false;

        Vector3 camDir = new(Mathf.Sin(_yaw), 0f, Mathf.Cos(_yaw));
        Vector3 forward = -camDir;
        Vector3 right = new(-forward.Z, 0f, forward.X);

        if (Input.IsActionPressed("cam_up"))    { _focus += forward * PanSpeed * dt; moved = true; }
        if (Input.IsActionPressed("cam_down"))  { _focus -= forward * PanSpeed * dt; moved = true; }
        if (Input.IsActionPressed("cam_left"))  { _focus -= right * PanSpeed * dt; moved = true; }
        if (Input.IsActionPressed("cam_right")) { _focus += right * PanSpeed * dt; moved = true; }

        if (Input.IsActionPressed("cam_rotate_cw"))  { _yaw += RotateSpeed * dt; moved = true; }
        if (Input.IsActionPressed("cam_rotate_ccw")) { _yaw -= RotateSpeed * dt; moved = true; }

        if (Input.IsKeyPressed(Key.Equal) || Input.IsKeyPressed(Key.KpAdd))
        { _distance = Mathf.Max(MinDistance, _distance - ZoomSpeed * dt); moved = true; }
        if (Input.IsKeyPressed(Key.Minus) || Input.IsKeyPressed(Key.KpSubtract))
        { _distance = Mathf.Min(MaxDistance, _distance + ZoomSpeed * dt); moved = true; }

        var vp = GetViewport();
        if (vp != null)
        {
            var mp = vp.GetMousePosition();
            var sz = vp.GetVisibleRect().Size;
            if (mp.X < EdgeMargin)       { _focus -= right * PanSpeed * dt; moved = true; }
            else if (mp.X > sz.X - EdgeMargin) { _focus += right * PanSpeed * dt; moved = true; }
            if (mp.Y < EdgeMargin)       { _focus -= forward * PanSpeed * dt; moved = true; }
            else if (mp.Y > sz.Y - EdgeMargin) { _focus += forward * PanSpeed * dt; moved = true; }
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
                    _distance = Mathf.Max(MinDistance, _distance - 32f);
                UpdateTransform();
            }
            else if (mb.ButtonIndex == MouseButton.WheelDown)
            {
                if (Input.IsKeyPressed(Key.Shift))
                    _yaw -= 0.3f;
                else
                    _distance = Mathf.Min(MaxDistance, _distance + 32f);
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

    private void UpdateTransform()
    {
        float hd = _distance * Mathf.Cos(_pitch);
        float vd = _distance * Mathf.Sin(-_pitch);
        Vector3 offset = new(hd * Mathf.Sin(_yaw), vd, hd * Mathf.Cos(_yaw));
        GlobalPosition = _focus + offset;
        LookAt(_focus, Vector3.Up);
    }
}

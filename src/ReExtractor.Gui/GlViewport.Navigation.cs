using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Avalonia.Input;
using Avalonia.Threading;

namespace ReExtractor.Gui;

public sealed partial class GlViewport
{
    private bool _looking;
    public float CameraSpeedScale { get; private set; } = 1f;

    public void SetCameraSpeed(float scale)
    {
        if (!float.IsFinite(scale)) return;
        var next = Math.Clamp(scale, 1f / 16f, 16f);
        if (Math.Abs(next - CameraSpeedScale) < 0.000001f) return;
        CameraSpeedScale = next;
        StateChanged?.Invoke();
    }

    public void HandleCameraWheel(double delta)
    {
        if (!double.IsFinite(delta) || delta == 0) return;
        if (_looking)
            SetCameraSpeed(CameraSpeedScale * MathF.Pow(2f, (float)Math.Clamp(delta, -32, 32) / 4f));
        else
            ZoomCamera(delta);
    }

    private readonly HashSet<Key> _navigationKeys = new();
    private DispatcherTimer? _navigationTimer;
    private long _navigationTimestamp;

    private void StartCameraNavigation()
    {
        _looking = true;
        _navigationTimestamp = Stopwatch.GetTimestamp();
        _navigationTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input,
            (_, _) =>
            {
                var now = Stopwatch.GetTimestamp();
                AdvanceCameraNavigation((float)Stopwatch.GetElapsedTime(_navigationTimestamp, now).TotalSeconds);
                _navigationTimestamp = now;
            });
        _navigationTimer.Start();
    }

    public void StopCameraNavigation()
    {
        _looking = false;
        _navigationKeys.Clear();
        _navigationTimer?.Stop();
    }

    public bool SetCameraNavigationKey(Key key, bool pressed)
    {
        if (key is not (Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E or Key.LeftShift or Key.RightShift))
            return false;
        if (!pressed) return _navigationKeys.Remove(key);
        if (!_looking) return false;
        _navigationKeys.Add(key);
        return true;
    }

    private void LookCamera(float dx, float dy)
    {
        // Rotate around the camera itself, preserving its world position.
        var eye = GetEye();
        CurrentView = ViewPreset.Perspective;
        Projection = ViewportProjection.Perspective;
        _yaw += dx * 0.005f;
        _pitch = Math.Clamp(_pitch + dy * 0.005f, -1.55f, 1.55f);
        _target = eye - new Vector3(MathF.Cos(_pitch) * MathF.Sin(_yaw),
            MathF.Cos(_pitch) * MathF.Cos(_yaw), MathF.Sin(_pitch)) * _dist;
        RequestNextFrameRendering();
        StateChanged?.Invoke();
    }

    private void AdvanceCameraNavigation(float seconds)
    {
        if (!_looking || !float.IsFinite(seconds) || seconds <= 0) return;
        int Axis(Key positive, Key negative) => (_navigationKeys.Contains(positive) ? 1 : 0)
            - (_navigationKeys.Contains(negative) ? 1 : 0);
        var forward = Vector3.Normalize(_target - GetEye());
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var movement = forward * Axis(Key.W, Key.S) + right * Axis(Key.D, Key.A)
            + Vector3.UnitZ * Axis(Key.E, Key.Q);
        if (movement.LengthSquared() < 0.0001f) return;
        CurrentView = ViewPreset.Perspective;
        Projection = ViewportProjection.Perspective;
        var speed = Math.Clamp((_boundsMax - _boundsMin).Length() / 3f, 0.1f, 200f) * CameraSpeedScale;
        if (_navigationKeys.Contains(Key.LeftShift) || _navigationKeys.Contains(Key.RightShift)) speed *= 3f;
        // Avoid jumps after a stalled UI frame and keep diagonal motion at the same speed.
        _target += Vector3.Normalize(movement) * speed * Math.Min(seconds, 0.1f);
        RequestNextFrameRendering();
    }
}

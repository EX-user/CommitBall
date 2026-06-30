using System.Windows;

namespace CommitBallBallShell;

public sealed class SpringBallAnimator : IBallAnimator
{
    private readonly Random _random = new();
    private double _eyeYaw;
    private double _eyePitch;
    private double _eyeYawVelocity;
    private double _eyePitchVelocity;
    private double _blink;
    private double _halfBlink;
    private bool _blinkClosing;
    private TimeSpan _nextBlinkAt = TimeSpan.FromSeconds(1.4);
    private TimeSpan _nextWanderAt = TimeSpan.Zero;
    private double _wanderYaw;
    private double _wanderPitch;

    public BallAnimationFrame Tick(TimeSpan now, TimeSpan delta, BallRuntimeState state, BallInputSnapshot input)
    {
        var dt = Math.Clamp(delta.TotalSeconds, 0.001, 0.05);
        var seconds = now.TotalSeconds;

        var targetYaw = 0.0;
        var targetPitch = 0.0;
        if (state.EyeEnabled && input.Bounds.Width > 1 && input.Bounds.Height > 1)
        {
            if (state.IsMouseIdle)
            {
                if (now >= _nextWanderAt)
                {
                    _wanderYaw = _random.NextDouble() * 2.35 - 1.175;
                    _wanderPitch = _random.NextDouble() * 1.72 - 0.86;
                    _nextWanderAt = now + TimeSpan.FromMilliseconds(700 + _random.Next(1700));
                }
                targetYaw = _wanderYaw;
                targetPitch = _wanderPitch;
            }
            else if (input.HasCursor)
            {
                var center = new Point(input.Bounds.Left + input.Bounds.Width / 2, input.Bounds.Top + input.Bounds.Height / 2);
                targetYaw = Math.Clamp((input.Cursor.X - center.X) / (input.Bounds.Width * 0.48), -1.0, 1.0) * 0.72;
                targetPitch = Math.Clamp((input.Cursor.Y - center.Y) / (input.Bounds.Height * 0.50), -1.0, 1.0) * 0.56;
            }
        }

        if (_halfBlink > 0.0)
        {
            _halfBlink = Math.Max(0.0, _halfBlink - dt * 2.8);
        }

        _eyeYaw = StepSpring(_eyeYaw, targetYaw, ref _eyeYawVelocity, 34.0, 7.5, dt);
        _eyePitch = StepSpring(_eyePitch, targetPitch, ref _eyePitchVelocity, 30.0, 7.0, dt);

        if (state.EyeEnabled && state.Mode == BallMode.Recording && now >= _nextBlinkAt && _blink <= 0.001)
        {
            _blinkClosing = true;
            _blink = 0.001;
        }

        if (_blink > 0.0)
        {
            var speed = _blinkClosing ? 8.5 : 5.2;
            _blink += (_blinkClosing ? 1.0 : -1.0) * speed * dt;
            if (_blink >= 1.0)
            {
                _blink = 1.0;
                _blinkClosing = false;
            }
            else if (_blink <= 0.0)
            {
                _blink = 0.0;
                _nextBlinkAt = now + TimeSpan.FromMilliseconds(1100 + _random.Next(3600));
            }
        }

        var pulse = state.Mode == BallMode.Recording ? Math.Sin(seconds * 5.4) * 0.5 + 0.5 : 0.0;
        var clickSquint = Ease(_halfBlink) * 0.56;
        var morph = Math.Clamp(Math.Max(Ease(_blink) * 0.86, clickSquint) + pulse * 0.14, 0.0, 1.0);

        return new BallAnimationFrame(pulse, _eyeYaw, _eyePitch, morph);
    }

    public void RequestHalfBlink()
    {
        _halfBlink = Math.Max(_halfBlink, 0.72);
    }

    public void Reset()
    {
        _eyeYaw = 0.0;
        _eyePitch = 0.0;
        _eyeYawVelocity = 0.0;
        _eyePitchVelocity = 0.0;
        _blink = 0.0;
        _halfBlink = 0.0;
        _blinkClosing = false;
        _nextBlinkAt = TimeSpan.FromSeconds(1.4);
        _nextWanderAt = TimeSpan.Zero;
    }

    private static double StepSpring(double value, double target, ref double velocity, double stiffness, double damping, double dt)
    {
        var force = (target - value) * stiffness;
        velocity += force * dt;
        velocity *= Math.Exp(-damping * dt);
        return value + velocity * dt;
    }

    private static double Ease(double value)
    {
        value = Math.Clamp(value, 0.0, 1.0);
        return 0.5 - Math.Cos(value * Math.PI) * 0.5;
    }
}

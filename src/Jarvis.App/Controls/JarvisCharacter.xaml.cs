using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Jarvis.Core.Agent;

namespace Jarvis.App.Controls;

public partial class JarvisCharacter : UserControl
{
    private readonly Rectangle[] _bars;
    private readonly DateTime _start = DateTime.UtcNow;
    private DateTime _lastFrame = DateTime.UtcNow;
    private DateTime _stateSince = DateTime.UtcNow;
    private double _nextBlink = 2;
    private double _blinkStart = -1;
    private Vector _look;
    private Vector _lookTarget;
    private double _nextLookChange = 1.5;
    private double _orbitAngle;
    private double _orbitOpacity;
    private double _audioLevel;
    private double _bobPhase;
    private double _bobAmplitude = 4;
    private double _bobSpeed = 1.8;
    private double _tilt;
    private double _eyeScale = 1;
    private double _squint = 1;
    private double _antennaPulse = 1;
    private double _mouthOpen;
    private double _hop;
    private double _hopVelocity;
    private double _squash;
    private double _squashVelocity;
    private double _antennaAngle;
    private double _antennaVelocity;
    private double _appear;
    private double _appearVelocity;
    private double _earScale = 1;
    private double _rippleOpacity;
    private double _happyUntil;
    private Color _color = StateColor(AssistantState.Idle);
    private AssistantState _state = AssistantState.Idle;

    public JarvisCharacter()
    {
        InitializeComponent();
        _bars = [Bar0, Bar1, Bar2, Bar3, Bar4];
        foreach (var dot in new[] { OrbitDot1, OrbitDot2, OrbitDot3 })
        {
            dot.Fill = new SolidColorBrush(_color);
        }

        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    public AssistantState State
    {
        get => _state;
        set
        {
            if (_state == value)
            {
                return;
            }

            // Finishing a task: brief happy "^ ^" eyes and a bigger hop.
            if (value == AssistantState.Idle && _state is AssistantState.Speaking or AssistantState.Acting or AssistantState.Thinking)
            {
                _happyUntil = Seconds + 1.4;
                _hopVelocity -= 50;
            }

            _state = value;
            _stateSince = DateTime.UtcNow;
            _antennaVelocity += Random.Shared.NextDouble() < 0.5 ? -160 : 160;

            // A little reaction on every state change: small hop + squash that springs back.
            _hopVelocity -= value is AssistantState.Error ? 0 : 70;
            _squashVelocity += value is AssistantState.Error ? -1.2 : 1.6;
        }
    }

    public float AudioLevel
    {
        set => _audioLevel = Math.Max(_audioLevel * 0.6, value);
    }

    /// <summary>Direction to look at (e.g. towards the AI pointer), roughly unit length.</summary>
    public void LookTowards(Vector direction)
    {
        if (direction.Length > 0)
        {
            direction.Normalize();
        }

        _lookTarget = direction * 7;
        _nextLookChange = Seconds + 2.5;
    }

    private double Seconds => (DateTime.UtcNow - _start).TotalSeconds;

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min(0.1, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;
        var t = Seconds;
        var inState = (now - _stateSince).TotalSeconds;

        AnimateColor(dt);
        AnimateBody(t, dt, inState);
        AnimateEyes(t, dt);
        AnimateMouth(t, dt);
        AnimateOrbit(dt);
        AnimateListening(t, dt);

        Badge.Opacity = Approach(Badge.Opacity, _state == AssistantState.Confirming ? 1 : 0, dt * 6);
        Canvas.SetTop(Badge, 4 + Math.Sin(t * 5) * 3);
        ErrorTint.Opacity = Approach(ErrorTint.Opacity, _state == AssistantState.Error ? 0.22 : 0, dt * 4);
        _audioLevel *= 0.92;
    }

    private void AnimateBody(double t, double dt, double inState)
    {
        var (amplitude, speed) = _state switch
        {
            AssistantState.Listening => (2.5, 4.5),
            AssistantState.Thinking or AssistantState.Transcribing => (3.0, 2.6),
            AssistantState.Acting => (2.0, 6.0),
            AssistantState.Speaking => (3.0, 3.8),
            _ => (4.0, 1.8),
        };

        // Accumulate phase and ease amplitude/speed so state changes never make the bob jump.
        _bobAmplitude = Damp(_bobAmplitude, amplitude, 4, dt);
        _bobSpeed = Damp(_bobSpeed, speed, 4, dt);
        _bobPhase = (_bobPhase + _bobSpeed * dt) % (Math.PI * 2);
        var y = Math.Sin(_bobPhase) * _bobAmplitude;

        // Damped springs for the state-change hop and squash.
        _hopVelocity += (-180 * _hop - 14 * _hopVelocity) * dt;
        _hop += _hopVelocity * dt;
        _squashVelocity += (-260 * _squash - 16 * _squashVelocity) * dt;
        _squash += _squashVelocity * dt;

        var x = 0.0;
        if (_state == AssistantState.Error && inState < 0.7)
        {
            x = Math.Sin(inState * 55) * 4 * (1 - inState / 0.7);
        }

        BodyTranslate.X = x;
        BodyTranslate.Y = y + _hop;

        var tilt = _state switch
        {
            AssistantState.Thinking or AssistantState.Transcribing => Math.Sin(t * 1.3) * 6,
            AssistantState.Listening => 4,
            AssistantState.Confirming => -6,
            AssistantState.Speaking => Math.Sin(t * 2.1) * 3,
            _ => Math.Sin(t * 0.9) * 2,
        };
        var previousTilt = _tilt;
        _tilt = Damp(_tilt, tilt + _look.X * 0.6, 5, dt);
        BodyRotate.Angle = _tilt;

        // Antenna follow-through: a spring on its base that lags behind head tilt and gets kicked on state changes.
        _antennaVelocity += (-120 * _antennaAngle - 9 * _antennaVelocity) * dt - (_tilt - previousTilt) * 25;
        _antennaAngle = Math.Clamp(_antennaAngle + _antennaVelocity * dt, -25, 25);
        AntennaRotate.Angle = _antennaAngle;

        // Pop-in on first show (underdamped spring 0 -> 1, overshoots slightly).
        _appearVelocity += (200 * (1 - _appear) - 16 * _appearVelocity) * dt;
        _appear = Math.Max(0, _appear + _appearVelocity * dt);

        // Breathing squash/stretch (volume-preserving) plus the reaction spring.
        var breath = 1 + Math.Sin(_bobPhase + Math.PI / 2) * 0.015 + Math.Clamp(_squash * 0.05, -0.08, 0.08);
        BodyScale.ScaleY = breath * _appear;
        BodyScale.ScaleX = _appear / breath;

        var lift = y + _hop;
        ShadowScale.ScaleX = ShadowScale.ScaleY = Math.Clamp(1 - lift / 40, 0.6, 1.2);
        Shadow.Opacity = Math.Clamp(1 - lift / 30, 0.4, 1);

        var pulse = _state is AssistantState.Idle
            ? 1 + Math.Sin(t * 1.8) * 0.04
            : 1 + Math.Sin(t * 8) * 0.18;
        _antennaPulse = Damp(_antennaPulse, pulse, 12, dt);
        AntennaScale.ScaleX = AntennaScale.ScaleY = _antennaPulse;
        AntennaGlow.BlurRadius = 12 + (_antennaPulse - 1) * 40;
    }

    private void AnimateEyes(double t, double dt)
    {
        if (_state is AssistantState.Thinking or AssistantState.Transcribing)
        {
            _lookTarget = new Vector(5 * Math.Cos(t * 1.5), -5);
        }
        else if (_state is AssistantState.Listening or AssistantState.Confirming)
        {
            _lookTarget = new Vector(0, 1);
        }
        else if (t > _nextLookChange)
        {
            _lookTarget = Random.Shared.NextDouble() < 0.4
                ? new Vector()
                : new Vector(Random.Shared.Next(-6, 7), Random.Shared.Next(-4, 5));
            _nextLookChange = t + 1.5 + Random.Shared.NextDouble() * 3;
        }

        _look += (_lookTarget - _look) * Math.Min(1, dt * 8);
        EyesTranslate.X = _look.X;
        EyesTranslate.Y = _look.Y;

        // Blink.
        if (_blinkStart < 0 && t > _nextBlink)
        {
            _blinkStart = t;
        }

        var eyeScaleY = 1.0;
        if (_blinkStart >= 0)
        {
            // Fast close, slightly slower open, eased.
            var p = (t - _blinkStart) / 0.18;
            var closed = p < 0.4 ? EaseInOut(p / 0.4) : 1 - EaseInOut((p - 0.4) / 0.6);
            eyeScaleY = p < 1 ? 1 - closed * 0.9 : 1;
            if (p >= 1)
            {
                _blinkStart = -1;
                _nextBlink = t + 2.5 + Random.Shared.NextDouble() * 3.5;
                if (Random.Shared.NextDouble() < 0.2)
                {
                    _nextBlink = t + 0.25;
                }
            }
        }

        var eyeScale = _state switch
        {
            AssistantState.Listening => 1.15,
            AssistantState.Acting => 0.85,
            AssistantState.Confirming => 1.1,
            _ => 1.0,
        };
        _eyeScale = Damp(_eyeScale, eyeScale, 10, dt);
        _squint = Damp(_squint, _state == AssistantState.Acting ? 0.7 : 1.0, 10, dt);

        LeftEyeScale.ScaleX = RightEyeScale.ScaleX = _eyeScale;
        LeftEyeScale.ScaleY = RightEyeScale.ScaleY = _eyeScale * eyeScaleY * _squint;

        LeftEyeShine.Opacity = RightEyeShine.Opacity = eyeScaleY * _squint > 0.6 ? 1 : 0;

        var error = _state == AssistantState.Error;
        var happy = _state == AssistantState.Idle && t < _happyUntil;
        Eyes.Opacity = Approach(Eyes.Opacity, error || happy ? 0 : 1, dt * 8);
        ErrorEyes.Opacity = Approach(ErrorEyes.Opacity, error ? 1 : 0, dt * 8);
        HappyEyes.Opacity = Approach(HappyEyes.Opacity, happy ? 1 : 0, dt * 8);
    }

    private void AnimateListening(double t, double dt)
    {
        var listening = _state == AssistantState.Listening;
        var level = Math.Clamp(_audioLevel * 9, 0, 1);

        // Ears perk up and pulse with the microphone level.
        _earScale = Damp(_earScale, listening ? 1.1 + level * 0.25 : 1, 14, dt);
        LeftEarScale.ScaleX = RightEarScale.ScaleX = LeftEarScale.ScaleY = RightEarScale.ScaleY = _earScale;

        // Two rings expanding outwards, half a cycle apart.
        _rippleOpacity = Approach(_rippleOpacity, listening ? 1 : 0, dt * 3);
        SetRipple(Ripple1, Ripple1Scale, t * 0.7 % 1);
        SetRipple(Ripple2, Ripple2Scale, (t * 0.7 + 0.5) % 1);
    }

    private void SetRipple(Ellipse ring, ScaleTransform scale, double phase)
    {
        scale.ScaleX = scale.ScaleY = 0.85 + phase * 0.6;
        ring.Opacity = _rippleOpacity * (1 - phase) * 0.55;
    }

    private void AnimateMouth(double t, double dt)
    {
        var listening = _state == AssistantState.Listening;
        var speaking = _state == AssistantState.Speaking;

        Equalizer.Opacity = Approach(Equalizer.Opacity, listening ? 1 : 0, dt * 8);
        OpenMouth.Opacity = Approach(OpenMouth.Opacity, speaking ? 1 : 0, dt * 8);
        Smile.Opacity = Approach(Smile.Opacity, listening || speaking ? 0 : 1, dt * 8);

        if (speaking)
        {
            // Layered, non-repeating "syllables" with short pauses instead of a single sine.
            var syllables = Math.Abs(Math.Sin(t * 11)) * (0.6 + 0.4 * Math.Sin(t * 3.7));
            var pause = Math.Sin(t * 1.3) > 0.85 ? 0.2 : 1;
            _mouthOpen = Damp(_mouthOpen, syllables * pause, 25, dt);
            OpenMouthScale.ScaleY = 0.3 + _mouthOpen;
            OpenMouthScale.ScaleX = 1.05 - _mouthOpen * 0.15;
        }

        if (listening)
        {
            var level = Math.Clamp(_audioLevel * 9, 0, 1);
            for (var i = 0; i < _bars.Length; i++)
            {
                var wobble = 0.55 + 0.45 * Math.Sin(t * 17 + i * 1.7);
                var height = 3 + (level * 16 + 2) * wobble * (i is 1 or 3 ? 0.85 : i == 2 ? 1 : 0.6);
                _bars[i].Height = height;
                Canvas.SetTop(_bars[i], 112 - height / 2);
            }
        }

        // A "hmm" flat mouth while thinking, a smile otherwise.
        var thinking = _state is AssistantState.Thinking or AssistantState.Transcribing or AssistantState.Confirming;
        Smile.Data = thinking ? FlatMouth : SmileMouth;
    }

    private void AnimateOrbit(double dt)
    {
        var (visible, speed) = _state switch
        {
            AssistantState.Thinking or AssistantState.Transcribing => (true, 160.0),
            AssistantState.Acting => (true, 320.0),
            _ => (false, 90.0),
        };
        _orbitOpacity = Approach(_orbitOpacity, visible ? 1 : 0, dt * 3);
        _orbitAngle = (_orbitAngle + speed * dt) % 360;
        Orbit.Opacity = _orbitOpacity;
        OrbitRotate.Angle = _orbitAngle;
    }

    private void AnimateColor(double dt)
    {
        var target = StateColor(_state);
        _color = Lerp(_color, target, Math.Min(1, dt * 6));
        AntennaBrush.Color = AntennaGlow.Color = _color;
        LeftEyeBrush.Color = RightEyeBrush.Color = LeftEyeGlow.Color = RightEyeGlow.Color = _color;
        SmileBrush.Color = OpenMouthBrush.Color = HappyEyesBrush.Color = RippleBrush.Color = _color;
        foreach (var dot in new[] { OrbitDot1, OrbitDot2, OrbitDot3 })
        {
            ((SolidColorBrush)dot.Fill).Color = _color;
        }
    }

    private static readonly Geometry SmileMouth = Geometry.Parse("M74,110 Q85,118 96,110");
    private static readonly Geometry FlatMouth = Geometry.Parse("M76,112 Q85,111 94,112");

    /// <summary>Accent colour per state; shared with the tray icon so both always match.</summary>
    internal static Color StateColor(AssistantState state) => state switch
    {
        AssistantState.Speaking => Color.FromRgb(0x5A, 0xC8, 0xFF),
        AssistantState.Listening => Color.FromRgb(0xFF, 0x4D, 0x8D),
        AssistantState.Transcribing => Color.FromRgb(0xFF, 0xC8, 0x57),
        AssistantState.Thinking => Color.FromRgb(0xA0, 0x7C, 0xFF),
        AssistantState.Acting => Color.FromRgb(0x00, 0xFF, 0xA3),
        AssistantState.Confirming => Color.FromRgb(0xFF, 0xB0, 0x20),
        AssistantState.Error => Color.FromRgb(0xFF, 0x3B, 0x3B),
        _ => Color.FromRgb(0x00, 0xE5, 0xFF),
    };

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Frame-rate independent exponential smoothing towards <paramref name="target"/>.</summary>
    private static double Damp(double value, double target, double rate, double dt) =>
        target + (value - target) * Math.Exp(-rate * dt);

    private static double EaseInOut(double x) => x * x * (3 - 2 * x);

    private static double Approach(double value, double target, double step) =>
        value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);
}

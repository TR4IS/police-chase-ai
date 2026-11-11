using System.Numerics;

namespace CopBotPlugin;

/// <summary>What the cop wants to do this moment.</summary>
public readonly record struct DriveCommand(
    Vector3 SteerTarget,
    float SpeedLimit,
    float LeaderInfluence,
    float LeaderGap,
    float LeaderSpeed,
    float GroundHeight,
    float Slope,
    float Roll);

/// <summary>
/// A car and the person driving it. The car moves only by integrating speed and heading from
/// wheel angle and pedals (kinematic bicycle), so position, speed and direction are continuous
/// however the instructions change. Steering is pure pursuit with human hands, the pedals are
/// the Intelligent Driver Model with a limit on how fast they can move.
/// </summary>
public sealed class CopDriver
{
    public const float Wheelbase = 2.9f;

    private const float MaxWheelAngle = 0.41f;
    private const float HandsSeconds = 0.1f;
    private const float MinLookahead = 7;
    private const float LimitSeconds = 0.15f;
    private const float StoppedSpeed = 0.3f;
    private const float BrakeTracking = 1;
    private const float PedalRiseRate = 12;
    private const float PedalReleaseRate = 40;
    private const float PedalBrakeRateCalm = 25;
    private const float PedalBrakeRateHot = 35;
    private const float PedalDelay = 0.2f;
    private const float MinRecklessness = 0.25f;
    private const float BodyDivePerAcceleration = 0.004f;
    private const float BodySeconds = 0.18f;
    private const float YawSeconds = 0.45f;
    private const float SlideGrip = 9;
    private const float MaxSlide = 25;
    private const float TurnAroundSpeed = 8;
    private const float ReverseSpeed = 4;
    private const float MaxReverseSeconds = 2.5f;
    private const float WreckBraking = 12;
    private const float WreckVeer = 0.3f;
    private const float WreckSpin = 3;
    private const float WreckSpinSeconds = 1.2f;
    private const float WreckSpinFullSpeed = 10;

    private float _steerFiltered;
    private float _limit;
    private float _gap;
    private float _leaderSpeed;
    private bool _hadLeader;
    private float _yawRate;
    private Vector3 _slide;
    private float _reverseTime;
    private Vector3 _skid;
    private float _spin;
    private float _skidFor;

    public CopDriver(Vector3 position, float heading, float speed)
    {
        Position = position;
        Heading = heading;
        Speed = speed;
    }

    public Vector3 Position { get; private set; }
    public float Heading { get; private set; }
    public float Speed { get; private set; }
    public float Acceleration { get; private set; }
    public float WheelAngle { get; private set; }
    public float Pitch { get; private set; }
    public float Roll { get; private set; }
    public float Throttle { get; private set; }
    public bool IsBraking => !IsWrecked && (_skidFor > 0 || Acceleration < -0.4f || Speed == 0);
    public bool IsSkidding => IsWrecked || _skidFor > 0;
    public float LateralAcceleration => Speed * Speed * MathF.Tan(MathF.Abs(WheelAngle)) / Wheelbase;
    public bool IsReversing { get; private set; }
    public bool IsWrecked { get; private set; }

    /// <summary>Which way the nose points.</summary>
    public Vector3 Direction => Forward(Heading);

    /// <summary>Which way the car is actually moving: the middle of the car swings wider than its nose points.</summary>
    public Vector3 Velocity => IsSkidding ? _skid * Speed : Forward(Heading + Slip) * Speed + _slide;

    private float Slip => MathF.Atan(0.5f * MathF.Tan(WheelAngle));

    public static float HeadingOf(Vector3 direction) => MathF.Atan2(direction.Z, direction.X) - MathF.PI / 2;

    public float LookaheadDistance(in DriverProfile profile) => MathF.Max(MinLookahead, MathF.Abs(Speed) * profile.LookaheadTime);

    public void Drive(in DriveCommand command, in DriverProfile profile, float dt)
    {
        if (IsSkidding)
        {
            SkidToRest(dt);
            SettleBody(command, dt);
            return;
        }

        var (sinAngle, cosAngle, distance) = Bearing(command.SteerTarget);
        UpdateTurnAround(sinAngle, cosAngle, dt);

        var speedLimit = Steer(sinAngle, cosAngle, distance, command.SpeedLimit, profile, dt);
        var acceleration = IsReversing ? ReverseThrottle() : Pedals(command, speedLimit, profile, dt);
        ApplyPedals(acceleration, profile, dt);
        Move(dt);
        SettleBody(command, dt);
    }

    /// <summary>A push from outside. Along the car it changes the speed, across it the tyres slide until they bite again.</summary>
    public void Shove(Vector3 kick, float yaw)
    {
        if (IsSkidding)
        {
            SetSkid(_skid * Speed + kick);
            _spin += yaw;
            return;
        }

        var forward = Forward(Heading);
        var along = Vector3.Dot(kick, forward);
        var speed = Speed + along;
        var leftOver = kick - forward * along + (speed < 0 && !IsReversing ? forward * speed : Vector3.Zero);
        Speed = IsReversing ? speed : MathF.Max(0, speed);

        _slide += leftOver;
        if (_slide.Length() > MaxSlide) _slide = Vector3.Normalize(_slide) * MaxSlide;
        _yawRate += yaw;
    }

    /// <summary>A shake of the wheel, the car's nose moves and the driver has to correct.</summary>
    public void Twitch(float yaw) => _yawRate += yaw;

    /// <summary>Pushed out from inside another car.</summary>
    public void Separate(Vector3 offset) => Position += offset;

    /// <summary>The driver has lost it: the car carries on the way it was going, spinning, on locked brakes, until it can be caught.</summary>
    public void LoseControl(float spin, float seconds)
    {
        BeginSkid(spin);
        _skidFor = seconds;
    }

    /// <summary>Out of the chase: the car spins away across the road and comes to rest.</summary>
    public void Wreck(float side)
    {
        BeginSkid(side * WreckSpin);
        _skid = Vector3.Transform(_skid, Quaternion.CreateFromAxisAngle(Vector3.UnitY, side * WreckVeer));
        IsWrecked = true;
    }

    private void BeginSkid(float spin)
    {
        SetSkid(Velocity);
        _slide = Vector3.Zero;
        _yawRate = 0;
        IsReversing = false;
        WheelAngle = 0;
        Acceleration = 0;
        Throttle = 0;
        _spin = spin;
    }

    private void SetSkid(Vector3 velocity)
    {
        Speed = velocity.Length();
        _skid = Speed > 0.01f ? velocity / Speed : Forward(Heading);
    }

    private void SkidToRest(float dt)
    {
        Speed = MathF.Max(0, Speed - WreckBraking * dt);
        Heading += _spin * MathF.Min(1, Speed / WreckSpinFullSpeed) * dt;
        _spin *= MathF.Max(0, 1 - dt / WreckSpinSeconds);
        Position += _skid * Speed * dt;

        if (IsWrecked) return;

        _skidFor -= dt;
        if (_skidFor <= 0 || Speed < 1) GatherItUp();
    }

    // Back in control: whatever isn't going the way the nose points is a slide the tyres still have to take out
    private void GatherItUp()
    {
        var velocity = _skid * Speed;
        var forward = Forward(Heading);
        _skidFor = 0;
        Speed = MathF.Max(0, Vector3.Dot(velocity, forward));
        _slide = velocity - forward * Speed;
        _limit = 0;
        _hadLeader = false;
        _steerFiltered = 0;
    }

    private (float Sin, float Cos, float Distance) Bearing(Vector3 target)
    {
        var to = new Vector2(target.X - Position.X, target.Z - Position.Z);
        var distance = MathF.Max(to.Length(), 0.01f);
        var forward = Forward(Heading);
        var turn = new Vector2(-MathF.Cos(Heading), -MathF.Sin(Heading));
        return ((to.X * turn.X + to.Y * turn.Y) / distance, (to.X * forward.X + to.Y * forward.Z) / distance, distance);
    }

    // Something well behind the car at low speed: a real driver does a three-point turn
    private void UpdateTurnAround(float sinAngle, float cosAngle, float dt)
    {
        if (!IsReversing && cosAngle < -0.6f && MathF.Abs(Speed) < 3)
        {
            IsReversing = true;
            _reverseTime = 0;
        }
        else if (IsReversing)
        {
            _reverseTime += dt;
            if (cosAngle > 0.4f || _reverseTime > MaxReverseSeconds) IsReversing = false;
        }
    }

    // Returns the speed the current turn allows
    private float Steer(float sinAngle, float cosAngle, float distance, float speedLimit, in DriverProfile profile, float dt)
    {
        float wanted;
        if (IsReversing)
        {
            // Back the tail away from the target so the nose swings onto it
            wanted = sinAngle >= 0 ? -MaxWheelAngle : MaxWheelAngle;
        }
        else
        {
            var bend = cosAngle < 0 ? MathF.Sign(sinAngle) : sinAngle;
            var curvature = 2 * bend / MathF.Max(distance, MinLookahead);
            var gripAngle = MathF.Atan(Wheelbase * profile.GripLimit / MathF.Max(Speed * Speed, 1));
            var maxAngle = MathF.Min(MaxWheelAngle, gripAngle);
            wanted = Math.Clamp(MathF.Atan(Wheelbase * curvature), -maxAngle, maxAngle);

            // Short-range steering overstates how tight the bend is, the road ahead already set the
            // real corner speed. This only stops the car taking a wild angle at speed.
            if (MathF.Abs(curvature) > 0.0001f) speedLimit = MathF.Min(speedLimit, MathF.Sqrt(2 * profile.GripLimit / MathF.Abs(curvature)));
            if (cosAngle < 0.3f) speedLimit = MathF.Min(speedLimit, TurnAroundSpeed);
        }

        _steerFiltered += (wanted - _steerFiltered) * MathF.Min(1, dt / HandsSeconds);
        var rate = profile.SteerRate * Math.Clamp(1.2f - MathF.Abs(Speed) / 45, 0.35f, 1);
        WheelAngle += Math.Clamp(_steerFiltered - WheelAngle, -rate * dt, rate * dt);
        return speedLimit;
    }

    private float Pedals(in DriveCommand command, float speedLimit, in DriverProfile profile, float dt)
    {
        var speed = Speed;
        if (speed < 0) return 4;

        var power = MathF.Min(profile.Acceleration, profile.PowerPerKg / MathF.Max(speed, 1));
        var hasLeader = command.LeaderInfluence > 0;
        var wantedLimit = speedLimit;

        if (hasLeader)
        {
            // A driver reacts to what the car ahead did a moment ago, not to every network packet
            var reaction = MathF.Min(1, dt / profile.ReactionTime);
            _gap = _hadLeader ? _gap + (command.LeaderGap - _gap) * reaction : command.LeaderGap;
            _leaderSpeed = _hadLeader ? _leaderSpeed + (command.LeaderSpeed - _leaderSpeed) * reaction : command.LeaderSpeed;

            // Never come up on a car faster than the brakes allow in the room left. A careful driver allows for
            // it stopping as well (Gipps). In a hot chase only for catching it up, stand on the brakes and get hit.
            var room = MathF.Max(0, _gap - profile.MinGap - speed * (profile.ReactionTime + PedalDelay));
            var careful = MathF.Sqrt(_leaderSpeed * _leaderSpeed + 2 * profile.Braking * room);
            var reckless = _leaderSpeed + MathF.Sqrt(2 * profile.Braking * room);
            var safe = careful + (reckless - careful) * MathF.Max(profile.Heat, MinRecklessness);
            wantedLimit = MathF.Min(wantedLimit, safe / command.LeaderInfluence);
        }

        wantedLimit = MathF.Max(wantedLimit, 0.01f);
        _limit = _limit == 0 ? wantedLimit : _limit + (wantedLimit - _limit) * MathF.Min(1, dt / LimitSeconds);

        // Below the limit stay flat out until nearly there. Above it, the limit is already a braking
        // curve, so brake as hard as the curve assumes and a bit more for any speed still over it.
        var over = speed - _limit;
        var ratio = speed / _limit;
        var ratioSquared = ratio * ratio;
        var acceleration = over <= 0
            ? power * (1 - ratioSquared * ratioSquared * ratioSquared * ratioSquared)
            : -(profile.Braking * MathF.Min(over, 1) + BrakeTracking * MathF.Max(0, over - 1));

        if (hasLeader)
        {
            // Pursuit driving: come in fast and brake late, only as much room as the brakes really need
            var closingSpeed = speed - _leaderSpeed;
            var closing = closingSpeed * MathF.Abs(closingSpeed) / (2 * profile.Braking);
            var wantedGap = profile.MinGap + MathF.Max(0, speed * profile.Headway + closing);
            var pressure = wantedGap / MathF.Max(_gap, 0.5f);
            acceleration -= command.LeaderInfluence * power * pressure * pressure;
        }

        _hadLeader = hasLeader;

        // Told to stay put: foot on the brake, no creeping
        if (_limit < StoppedSpeed) acceleration = MathF.Min(acceleration, 0);

        return Math.Clamp(acceleration, -profile.HardBraking, power);
    }

    private float ReverseThrottle() => Math.Clamp((-ReverseSpeed - Speed) * 2, -6, 3);

    private void ApplyPedals(float wanted, in DriverProfile profile, float dt)
    {
        // Coming off the brakes takes a moment, feeding in the power takes longer, standing on the brakes depends how hot it is
        var rate = wanted <= Acceleration ? PedalBrakeRateCalm + (PedalBrakeRateHot - PedalBrakeRateCalm) * profile.Heat
            : Acceleration < 0 ? PedalReleaseRate : PedalRiseRate;
        Acceleration += Math.Clamp(wanted - Acceleration, -rate * dt, rate * dt);

        var next = Speed + Acceleration * dt;
        if (!IsReversing && Speed >= 0 && (next < 0 || (next < StoppedSpeed && wanted <= 0)))
        {
            next = 0;
            Acceleration = 0;
        }

        Speed = next;
        Throttle = Math.Clamp(Acceleration / MathF.Max(profile.Acceleration, 0.1f), 0, 1);
    }

    private void Move(float dt)
    {
        var slip = Slip;
        Heading += (2 * Speed / Wheelbase * MathF.Sin(slip) + _yawRate) * dt;
        _yawRate *= MathF.Max(0, 1 - dt / YawSeconds);
        Position += (Forward(Heading + slip) * Speed + _slide) * dt;

        var sliding = _slide.Length();
        _slide = sliding > SlideGrip * dt ? _slide * (1 - SlideGrip * dt / sliding) : Vector3.Zero;
    }

    private void SettleBody(in DriveCommand command, float dt)
    {
        var body = MathF.Min(1, dt / BodySeconds);
        Position = Position with { Y = Position.Y + (command.GroundHeight - Position.Y) * body };
        Pitch += (command.Slope - Acceleration * BodyDivePerAcceleration - Pitch) * body;
        Roll += (command.Roll - Roll) * body;
    }

    private static Vector3 Forward(float heading) => new(-MathF.Sin(heading), 0, MathF.Cos(heading));
}

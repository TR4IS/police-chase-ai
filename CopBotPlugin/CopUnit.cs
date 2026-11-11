using System.Numerics;

namespace CopBotPlugin;

public enum CopRole
{
    /// <summary>Sit right on the player's bumper.</summary>
    Tail,

    /// <summary>Hold the next lane just behind the player, cutting off the way out.</summary>
    Flank,

    /// <summary>Pull up beside the player in the next lane. In a hot chase, lean on them.</summary>
    Alongside,

    /// <summary>Overtake in the next lane, pull in front and slow the player down.</summary>
    Block,

    /// <summary>Hit the player's rear corner, drop back, and do it again.</summary>
    Ram,

    /// <summary>Parked across the road ahead, joins the chase once the player is past.</summary>
    Roadblock,

    /// <summary>Comes down the player's lane the wrong way, then turns round and joins the chase.</summary>
    HeadOn
}

/// <summary>One cop in a chase: decides what to do on the road and tells the driver.</summary>
public sealed class CopUnit
{
    private const float CarLength = 4.9f;
    private const float LaneOverlapStart = 2.6f;
    private const float LaneOverlapFull = 1.4f;
    private const float LostDistance = 120;
    private const float OffRouteDistance = 18;
    private const float SearchBack = 400;
    private const float EngageDistance = 100;
    private const float SlowPlayerSpeed = 8;
    private const float SideHoldSeconds = 2.5f;
    private const float LaneGapSeconds = 1.5f;

    private const float JoinBehindFirst = 120;
    private const float JoinBehindPerCop = 40;
    private const float JoinSpeedMargin = 0.9f;
    private const float JoinMinSpeed = 15;

    // Far behind, a driver takes more risks to catch up, then calms down again
    private const float CatchUpFrom = 40;
    private const float CatchUpFull = 180;
    private const float CatchUpHeat = 0.4f;

    // How much faster than the player a cop takes a bend the player has just been through
    private const float ProvenMargin = 1.06f;
    private const float ProvenMarginFar = 0.3f;

    private const float TurnBackCalm = 18;
    private const float TurnBackHot = 35;

    private const float RamRange = 40;
    private const float RamClosing = 6;
    private const float RamClosingHot = 6;
    private const long RamAttackMs = 3000;
    private const long BackOffLeastMs = 1200;
    private const long BackOffExtraMs = 2500;
    private const float StalkNear = 8;
    private const float StalkFar = 18;
    private const float SettledStress = 0.35f;
    private const float RamDropBack = 3;
    private const float RamEaseOff = 0.5f;

    private const float BlockPassedAt = 6;
    private const float BlockLead = 12;
    private const float BlockSlowing = 1.5f;
    private const float BlockSlowingHot = 2.5f;
    private const float BlockMaxSlower = 12;

    private const float FlankBack = 14;

    private const float SwipeFromHeat = 0.7f;
    private const float SwipeDistance = 1.5f;
    private const float SwipeLevel = 3;
    private const long SwipeMs = 1500;
    private const long SwipeLeastMs = 2000;
    private const long SwipeExtraMs = 5000;

    // A hit is felt by how much it moved the car and how much it turned it
    private const float ShockFullKick = 15;
    private const float ShockFullYaw = 8;
    private const float BracedShock = 0.5f;

    // A sudden move by the player is a change in what they were doing, close up, at speed
    private const float SurpriseFastSeconds = 0.15f;
    private const float SurpriseSlowSeconds = 0.8f;
    private const float SurpriseFull = 40;
    private const float SurpriseRange = 70;
    private const float SurpriseFullSpeed = 45;
    private const float LoadFullGrip = 20;
    private const float LoadWeight = 0.3f;

    private const float WobbleYaw = 0.25f;
    private const float LostTrackYaw = 0.4f;
    private const long HoldStraightMs = 500;
    private const float LostTrackPace = 0.8f;
    private const float SeesThroughDazeWithin = 15;

    private const float PushOutRate = 5;
    private const float PushOutSpeed = 8;
    private const float MinClosing = 0.5f;
    private const long PushRepeatMs = 250;
    private const float KnockedAsideFrom = 40;

    private const float RoadblockPassedBy = 15;
    private const float RoadblockGiveUp = 800;
    private const long RoadblockWaitMs = 60_000;

    private const float HeadOnSpeed = 45;
    private const float HeadOnSpeedHot = 15;
    private const float HeadOnAimRange = 200;
    private const float HeadOnCommitSeconds = 0.8f;
    private const float HeadOnPassedWithin = 150;
    private const float HeadOnStopGap = 12;
    private const long HeadOnMs = 25_000;

    private const float DamageIgnored = 8;
    private const float RamDamageTaken = 0.4f;
    private const float WreckAfter = 100;
    private const float WreckAfterHot = 60;
    private const long HitRepeatMs = 400;

    private const float SeesWithin = 60;
    private const float SightRange = 300;
    private const float SightCorridor = 12;
    private const int SightSamples = 5;

    private long _recoilUntil;
    private float _pitSide = 1;
    private float _favouredSide = 1;
    private float _side;
    private float _sideHold;
    private float _laneMissing;
    private float _blockSpeed;
    private bool _inFront;
    private bool _wasInFront;
    private long _swipeUntil;
    private long _nextSwipe;
    private float _heat;
    private float _damage;
    private long _lastHit;
    private long _ambushUntil;
    private Vector3 _roadDirection;
    private List<Vector3> _path = new();
    private int _pathIndex;
    private long _attackUntil;
    private long _dazedUntil;
    private long _holdStraightUntil;
    private long _lastPush;
    private long _now;
    private float _stalkGap;
    private Vector3 _lastPlayerVelocity;
    private Vector3 _accelerationFast;
    private Vector3 _accelerationSlow;

    public CopUnit(CopDriver driver, float progress)
    {
        Driver = driver;
        Progress = progress;
        Nerve = new DriverNerve(Dice);
        _stalkGap = CarLength + Nerve.Between(StalkNear, StalkFar);
    }

    /// <summary>Where each driver's character and mistakes come from.</summary>
    public static Random Dice { get; set; } = new();

    public DriverNerve Nerve { get; }

    /// <summary>What just went wrong, for the log. Cleared by whoever reads it.</summary>
    public string? Incident { get; set; }

    public CopDriver Driver { get; }
    public CopRole Role { get; set; }
    public float Progress { get; private set; }
    public float Lateral { get; private set; }
    public bool IsLost { get; private set; }
    public long WreckedAt { get; private set; }
    public bool IsWrecked => Driver.IsWrecked;
    public bool IsAmbush => Role is CopRole.Roadblock or CopRole.HeadOn;
    public bool IsChasing => !IsWrecked && !IsAmbush;

    public static CopUnit ParkedAcrossRoad(CopDriver driver, Vector3 roadDirection, long now) =>
        new(driver, 0) { Role = CopRole.Roadblock, _roadDirection = roadDirection, _ambushUntil = now + RoadblockWaitMs };

    public static CopUnit ComingHeadOn(CopDriver driver, List<Vector3> path, long now)
    {
        path.Reverse();
        return new CopUnit(driver, 0) { Role = CopRole.HeadOn, _path = path, _ambushUntil = now + HeadOnMs };
    }

    /// <summary>The speed a cop comes down the wrong way at.</summary>
    public static float HeadOnSpeedAt(float heat) => HeadOnSpeed + HeadOnSpeedHot * heat;

    /// <summary>How many cops a chase gets.</summary>
    public static int UnitsForStars(int stars) => stars switch { <= 1 => 2, 2 => 3, 3 => 4, 4 => 5, _ => 6 };

    /// <summary>How far back along the road a new cop comes in, out of sight.</summary>
    public static float JoinBehind(int copsAlready) => JoinBehindFirst + copsAlready * JoinBehindPerCop;

    /// <summary>A new cop comes in already at speed, but one it can take the road ahead at.</summary>
    public static float JoinSpeed(PlayerRoute route, float progress, float heat)
    {
        var profile = DriverProfile.ForHeat(heat);
        var pace = profile.Pace(route.PlayerSpeed, route.HeadDistance - progress);
        var bend = route.CornerSpeedLimit(progress, pace, profile.CornerGrip, profile.Braking, ProvenMargin) * JoinSpeedMargin;
        return MathF.Max(MathF.Min(pace, bend), JoinMinSpeed);
    }

    /// <summary>
    /// Who does what is decided by who is nearest the player when the chase changes, and only gets
    /// serious as the stars go up. Once given a job a cop sticks to it, or they would keep changing their minds.
    /// </summary>
    public static void AssignRoles(List<CopUnit> units, int stars, float head)
    {
        // Anyone already past the player goes last, ramming takes being behind them
        var chasers = units.Where(unit => unit.IsChasing).OrderByDescending(unit => unit.Progress > head ? -unit.Progress : unit.Progress).ToList();
        for (var rank = 0; rank < chasers.Count; rank++)
        {
            chasers[rank]._favouredSide = rank % 2 == 0 ? 1 : -1;
            chasers[rank].Role = (stars, rank) switch
            {
                ( <= 1, 1) => CopRole.Flank,
                (2, 1) => CopRole.Alongside,
                (2, 2) => CopRole.Block,
                (3, 0) => CopRole.Ram,
                (3, 1) => CopRole.Block,
                (3, 2) => CopRole.Alongside,
                (3, 3) => CopRole.Flank,
                ( >= 4, 0) => CopRole.Ram,
                ( >= 4, 1) => CopRole.Block,
                ( >= 4, 2) => CopRole.Alongside,
                ( >= 4, 3) => CopRole.Alongside,
                ( >= 4, 4) => CopRole.Ram,
                ( >= 4, 5) => CopRole.Block,
                _ => CopRole.Tail
            };
        }
    }

    public void Update(PlayerRoute route, List<CopUnit> units, float heat, long now, float dt)
    {
        _heat = heat;
        _now = now;
        FeelContact(route, now, dt);

        if (IsWrecked)
        {
            Driver.Drive(new DriveCommand(Driver.Position, 0, 0, 0, 0, Driver.Position.Y, 0, Driver.Roll), DriverProfile.ForHeat(heat), dt);
        }
        else if (Role == CopRole.Roadblock)
        {
            WaitAcrossRoad(route, heat, now, dt);
        }
        else if (Role == CopRole.HeadOn)
        {
            ChargeHeadOn(route, heat, now, dt);
        }
        else
        {
            Chase(route, units, heat, now, dt);
        }
    }

    /// <summary>The game reports contact with the player. It takes damage, and if the push hasn't been felt already it's felt now.</summary>
    public void OnHit(Vector3 playerPosition, Vector3 playerDirection, float impactKmh, long now)
    {
        if (IsWrecked || now - _lastHit < HitRepeatMs) return;
        _lastHit = now;

        var touch = CarContact.Measure(Driver.Position, Driver.Direction, playerPosition, playerDirection);
        if (now - _lastPush > PushRepeatMs) Push(touch, impactKmh / 3.6f, now);

        // The ones doing the ramming have push bars on
        if (impactKmh >= DamageIgnored) _damage += Role == CopRole.Ram ? impactKmh * RamDamageTaken : impactKmh;

        if (_damage >= WreckAfter + WreckAfterHot * _heat)
        {
            WreckedAt = now;
            Driver.Wreck(Nerve.EitherSide());
        }
        else if (IsAmbush && impactKmh >= KnockedAsideFrom)
        {
            // Nobody at the wheel of a parked car to hold it straight
            Driver.LoseControl(Nerve.EitherSide() * WreckSpinOf(impactKmh), 2);
        }
    }

    /// <summary>What the player's car does to this one when they overlap: momentum and spin, whoever started it.</summary>
    private void FeelContact(PlayerRoute route, long now, float dt)
    {
        if (IsWrecked) return;

        var touch = CarContact.Measure(Driver.Position, Driver.Direction, route.PlayerPosition, route.PlayerDirection);
        if (touch.Penetration <= 0) return;

        // However it got here, this car can't stay inside the other one. Leaning on it moves it.
        Driver.Separate(touch.Normal * MathF.Min(touch.Penetration * PushOutRate, PushOutSpeed) * dt);

        var closing = Vector3.Dot((route.PlayerVelocity - Driver.Velocity) with { Y = 0 }, touch.Normal);
        if (closing > MinClosing && now - _lastPush > PushRepeatMs) Push(touch, closing, now);
    }

    private void Push(in CarContact.Touch touch, float closing, long now)
    {
        _lastPush = now;
        var (kick, yaw) = CarContact.Impulse(touch, closing);
        Driver.Shove(kick, yaw);

        // A driver going for the hit is braced for it, one who gets hit isn't
        var shock = kick.Length() / ShockFullKick + MathF.Abs(yaw) / ShockFullYaw;
        Nerve.Shock(now < _attackUntil ? shock * BracedShock : shock);

        // Anyone who has just been in a collision thinks again before the next one
        _attackUntil = 0;
        _swipeUntil = 0;
        _recoilUntil = now + Nerve.SampleMs(BackOffLeastMs, BackOffExtraMs);
    }

    private static float WreckSpinOf(float impactKmh) => MathF.Min(4, impactKmh / 30);

    // A sudden move is the player's car doing something different from what it was just doing.
    // A steady bend or steady braking stops being a surprise, and it matters less from far away.
    private float Surprise(PlayerRoute route, float distance, float dt)
    {
        var acceleration = (route.PlayerVelocity - _lastPlayerVelocity) / MathF.Max(dt, 0.001f);
        _lastPlayerVelocity = route.PlayerVelocity;
        _accelerationFast += (acceleration - _accelerationFast) * MathF.Min(1, dt / SurpriseFastSeconds);
        _accelerationSlow += (acceleration - _accelerationSlow) * MathF.Min(1, dt / SurpriseSlowSeconds);

        var nearness = Math.Clamp(1 - distance / SurpriseRange, 0, 1);
        var haste = Math.Clamp(MathF.Abs(Driver.Speed) / SurpriseFullSpeed, 0.3f, 1.2f);
        return (_accelerationFast - _accelerationSlow).Length() / SurpriseFull * nearness * haste;
    }

    private void Suffer(Mishap mishap, long now)
    {
        if (mishap == Mishap.None) return;

        var side = Nerve.EitherSide();
        Incident = $"{mishap} at risk {Nerve.Risk * 100:F0}%";
        _attackUntil = 0;
        _swipeUntil = 0;
        switch (mishap)
        {
            case Mishap.Wobble:
                Driver.Twitch(side * WobbleYaw);
                _dazedUntil = now + Nerve.SampleMs(400, 500);
                break;
            case Mishap.LostTrack:
                Driver.Twitch(side * LostTrackYaw);
                _dazedUntil = now + Nerve.SampleMs(1500, 2000);
                _holdStraightUntil = now + HoldStraightMs;
                break;
            case Mishap.Spin:
                Driver.LoseControl(side * Nerve.Between(1.5f, 4), Nerve.Between(1, 5));
                _dazedUntil = now + Nerve.SampleMs(3000, 2000);
                break;
        }
    }

    /// <summary>
    /// Whether this cop has eyes on the player: close, or with the road between them running straight
    /// enough to see down. The server doesn't know the buildings, but a road that bends far away from
    /// the straight line between two cars has something in the way.
    /// </summary>
    public bool Sees(PlayerRoute route)
    {
        if (Driver.IsSkidding) return false;

        var to = (route.PlayerPosition - Driver.Position) with { Y = 0 };
        var distance = to.Length();
        if (_now < _dazedUntil && distance > SeesThroughDazeWithin) return false;
        if (distance < SeesWithin) return true;
        if (distance > SightRange || IsAmbush) return false;

        var gap = route.HeadDistance - Progress;
        for (var i = 1; i < SightSamples; i++)
        {
            var point = (route.Sample(Progress + gap * i / SightSamples).Position - Driver.Position) with { Y = 0 };
            if (MathF.Abs(point.X * to.Z - point.Z * to.X) / distance > SightCorridor) return false;
        }

        return true;
    }

    private void Chase(PlayerRoute route, List<CopUnit> units, float heat, long now, float dt)
    {
        var distance = (route.PlayerPosition - Driver.Position with { Y = route.PlayerPosition.Y }).Length();
        FindPlaceOnRoute(route, distance, heat);

        var behind = route.HeadDistance - Progress;
        var catchUp = Math.Clamp((behind - CatchUpFrom) / (CatchUpFull - CatchUpFrom), 0, 1);
        var profile = DriverProfile.ForHeat(heat + catchUp * CatchUpHeat).CatchingUp(catchUp);

        var surprise = Surprise(route, distance, dt);
        if (!Driver.IsSkidding)
        {
            var load = Driver.LateralAcceleration / LoadFullGrip * (1 - catchUp);
            Suffer(Nerve.Roll(surprise, load * load * LoadWeight, heat, dt), now);
        }

        _sideHold -= dt;
        var plan = PlanRole(route, profile, profile.Pace(route.PlayerSpeed, behind), now, dt);
        var laneRate = profile.LaneChangeRate * dt;
        Lateral += Math.Clamp(plan.Lateral - Lateral, -laneRate, laneRate);

        var target = plan.Aim ?? PointAhead(route, profile);
        var cornerLimit = route.CornerSpeedLimit(Progress, MathF.Abs(Driver.Speed), profile.CornerGrip, profile.Braking, ProvenMargin + ProvenMarginFar * catchUp);
        var limit = MathF.Min(plan.SpeedGoal, cornerLimit);
        var (influence, gap, leaderSpeed) = FindLeader(route, units, plan.FollowPlayer);

        var here = route.Sample(Progress);
        var slope = (route.Sample(Progress + 3).Position.Y - route.Sample(Progress - 3).Position.Y) / 6;

        Driver.Drive(new DriveCommand(target, limit, influence, gap, leaderSpeed, here.Position.Y, slope, here.Roll), profile, dt);
    }

    private void FindPlaceOnRoute(PlayerRoute route, float distance, float heat)
    {
        Progress = route.Project(Driver.Position, Progress, out var offRoute);

        // Overshot a turn, or the player came back past on the same road: turn round and go after them.
        // Only when close enough to be the same road, a parallel street is not a shortcut.
        var turnBack = TurnBackCalm + (TurnBackHot - TurnBackCalm) * heat;
        var routeGap = route.HeadDistance - Progress;
        if (offRoute > OffRouteDistance || (distance < turnBack && routeGap > distance * 3 + 40))
        {
            Progress = route.ProjectNearest(Driver.Position, SearchBack, out offRoute);
        }

        IsLost = offRoute > LostDistance;
    }

    private readonly record struct Plan(float SpeedGoal, float Lateral, bool FollowPlayer, Vector3? Aim);

    private Plan PlanRole(PlayerRoute route, in DriverProfile profile, float pace, long now, float dt)
    {
        var head = route.HeadDistance;
        var gap = head - Progress - CarLength;
        var moving = route.PlayerSpeed >= SlowPlayerSpeed;
        if (!_inFront) _wasInFront = false;

        // Getting in front takes a clear lead, staying in front only takes not being passed
        _inFront = Role == CopRole.Block && (_inFront ? Progress > head : Progress >= head + BlockPassedAt);

        // Rattled and lost track: off the throttle and carrying straight on for a moment, whatever the job was
        if (now < _dazedUntil)
        {
            Vector3? straight = now < _holdStraightUntil ? Driver.Position + Driver.Direction * 40 : null;
            return new Plan(MathF.Min(pace, route.PlayerSpeed * LostTrackPace), Lateral, MathF.Abs(Lateral) < LaneOverlapStart, straight);
        }

        // Those working the next lane drive in it from the start, or they would queue behind the ones ahead
        switch (Role)
        {
            case CopRole.Flank when FindNextLane(route, dt, out var side):
                return new Plan(gap < EngageDistance ? route.PlayerSpeed + Arrive(head - FlankBack - Progress, profile, 18) : pace, side, MathF.Abs(Lateral) < LaneOverlapStart, null);

            case CopRole.Alongside when FindNextLane(route, dt, out var side):
                var lateral = SwipeOr(side, head - Progress, profile.Heat, now);
                var leaning = now < _swipeUntil;
                return new Plan(gap < EngageDistance ? route.PlayerSpeed + Arrive(head - Progress, profile, 18) : pace, lateral, !leaning && MathF.Abs(Lateral) < LaneOverlapStart, null);

            case CopRole.Block when _inFront:
                // Back into the player's lane and take the speed away, as soon as it's right in front of them
                var slowed = _blockSpeed - (BlockSlowing + BlockSlowingHot * profile.Heat) * dt;
                _blockSpeed = _wasInFront && Progress - head < BlockLead * 2 ? MathF.Max(route.PlayerSpeed - BlockMaxSlower, slowed) : route.PlayerSpeed;
                _wasInFront = true;
                return new Plan(MathF.Min(route.PlayerSpeed + Arrive(head + BlockLead - Progress, profile, 6), MathF.Max(_blockSpeed, 0)), 0, false, null);

            // A player who has nearly stopped is already boxed in, there is no need to get past them
            case CopRole.Block when (route.HasRoadAhead || !moving) && FindNextLane(route, dt, out var side):
                var goal = moving ? head + BlockLead : head;
                return new Plan(gap < EngageDistance ? route.PlayerSpeed + Arrive(goal - Progress, profile, 20) : pace, side, MathF.Abs(Lateral) < LaneOverlapStart, null);

            // Ramming is for a driver who won't stop. Someone stopping just gets arrested.
            case CopRole.Ram when moving && head > Progress:
                // Drop back gently after contact, no slamming on the brakes
                if (now < _recoilUntil) return new Plan(MathF.Max(route.PlayerSpeed - RamDropBack, Driver.Speed - RamEaseOff), 0, false, null);
                if (gap >= RamRange) break;

                // Sit a little way back and pick the moment, with the car settled, rather than leaning on the bumper
                if (now >= _attackUntil && Nerve.Stress < SettledStress && Nerve.DecidesToAttack(profile.Heat, dt)) _attackUntil = now + RamAttackMs;
                return now < _attackUntil
                    ? new Plan(route.PlayerSpeed + RamClosing + RamClosingHot * profile.Heat, 0, false, RearCorner(route))
                    : new Plan(route.PlayerSpeed + Arrive(head - _stalkGap - Progress, profile, 15), 0, true, null);
        }

        // A tail that finds itself in front eases off and lets the player come past
        return Progress > head
            ? new Plan(route.PlayerSpeed * 0.8f, 0, false, null)
            : new Plan(pace, 0, true, null);
    }

    // Speed to add to the player's so as to arrive at a place along the road without overshooting.
    // Far away that's a braking curve, up close it eases in.
    private static float Arrive(float distance, in DriverProfile profile, float maxExtra)
    {
        var size = MathF.Abs(distance);
        return MathF.Sign(distance) * MathF.Min(MathF.Min(MathF.Sqrt(2 * profile.Braking * size), 0.6f * size), maxExtra);
    }

    // Level with someone who won't stop: lean on them, then back to the lane and try again
    private float SwipeOr(float side, float level, float heat, long now)
    {
        if (now < _swipeUntil) return MathF.Sign(side) * SwipeDistance;

        if (heat >= SwipeFromHeat && now >= _nextSwipe && MathF.Abs(level) < SwipeLevel && Nerve.Stress < SettledStress)
        {
            _swipeUntil = now + SwipeMs;
            _nextSwipe = _swipeUntil + Nerve.SampleMs(SwipeLeastMs, SwipeExtraMs);
        }

        return side;
    }

    // A PIT: aim for the corner of the player's rear bumper, not straight through the car
    private Vector3 RearCorner(PlayerRoute route)
    {
        var side = new Vector3(route.PlayerDirection.Z, 0, -route.PlayerDirection.X);
        var offset = Vector3.Dot(Driver.Position - route.PlayerPosition, side);
        if (MathF.Abs(offset) > 0.3f) _pitSide = MathF.Sign(offset);

        return route.PlayerPosition + route.PlayerVelocity * 0.25f - route.PlayerDirection * 2.2f + side * (_pitSide * 0.8f);
    }

    // The next lane over, if there is one. A driver who has picked a side keeps it for a while,
    // and short gaps in the lane data don't send them back across the road.
    private bool FindNextLane(PlayerRoute route, float dt, out float side)
    {
        side = _side;
        if (_sideHold > 0 && _side != 0) return true;

        // Is there a next lane where this cop is, the road may be different back there
        var tangent = route.Tangent(Progress);
        var right = new Vector3(tangent.Z, 0, -tangent.X);
        if (route.Road.TryFindAdjacentLane(route.Sample(Progress).Position, tangent, right * _favouredSide, out var offset))
        {
            _side = Vector3.Dot(offset, right);
            _sideHold = SideHoldSeconds;
            _laneMissing = 0;
            side = _side;
            return true;
        }

        _laneMissing += dt;
        if (_side != 0 && _laneMissing < LaneGapSeconds) return true;

        _side = 0;
        side = 0;
        return false;
    }

    private Vector3 PointAhead(PlayerRoute route, in DriverProfile profile)
    {
        var along = Progress + Driver.LookaheadDistance(profile);
        var point = route.Sample(along).Position;
        if (Lateral == 0) return point;

        var tangent = route.Tangent(along);
        return point + new Vector3(tangent.Z, 0, -tangent.X) * Lateral;
    }

    // The nearest car ahead in this lane. A car that is only partly in the lane counts partly,
    // so a lane change brings the driver onto the brakes gradually rather than all at once.
    private (float Influence, float Gap, float Speed) FindLeader(PlayerRoute route, List<CopUnit> units, bool followPlayer)
    {
        var influence = 0f;
        var gap = float.MaxValue;
        var speed = 0f;

        void Consider(float between, float carSpeed, float lateralApart)
        {
            var overlap = Math.Clamp((LaneOverlapStart - MathF.Abs(lateralApart)) / (LaneOverlapStart - LaneOverlapFull), 0, 1);
            if (overlap <= 0 || between >= gap) return;

            influence = overlap;
            gap = between;
            speed = carSpeed;
        }

        if (followPlayer && route.HeadDistance > Progress)
        {
            Consider(route.HeadDistance - Progress - CarLength, route.PlayerSpeed, Lateral);
        }

        foreach (var other in units)
        {
            if (other != this && other.IsChasing && !other.Driver.IsSkidding && other.Progress > Progress)
            {
                Consider(other.Progress - Progress - CarLength, other.Driver.Speed, other.Lateral - Lateral);
            }
        }

        return (influence, MathF.Max(gap, 0.5f), speed);
    }

    private void JoinChase(PlayerRoute route)
    {
        Role = CopRole.Tail;
        Progress = route.ProjectNearest(Driver.Position, SearchBack, out var offRoute);
        IsLost = offRoute > LostDistance;
    }

    private void WaitAcrossRoad(PlayerRoute route, float heat, long now, float dt)
    {
        var to = (route.PlayerPosition - Driver.Position) with { Y = 0 };
        var distance = to.Length();
        if (Vector3.Dot(to, _roadDirection) > RoadblockPassedBy && distance < LostDistance)
        {
            JoinChase(route);
            return;
        }

        IsLost = now > _ambushUntil || distance > RoadblockGiveUp;
        Driver.Drive(new DriveCommand(Driver.Position + Driver.Direction * 20, 0, 0, 0, 0, Driver.Position.Y, 0, 0), DriverProfile.ForHeat(heat), dt);
    }

    private void ChargeHeadOn(PlayerRoute route, float heat, long now, float dt)
    {
        var profile = DriverProfile.ForHeat(heat);
        var to = (route.PlayerPosition - Driver.Position) with { Y = 0 };
        var distance = to.Length();

        while (_pathIndex + 1 < _path.Count
               && Vector3.DistanceSquared(_path[_pathIndex + 1], Driver.Position) < Vector3.DistanceSquared(_path[_pathIndex], Driver.Position))
        {
            _pathIndex++;
        }

        // Missed, or the player isn't coming: turn round and chase like everyone else
        var missed = Vector3.Dot(to, Driver.Direction) < 0 && distance < HeadOnPassedWithin;
        if (missed || _pathIndex >= _path.Count - 1 || now > _ambushUntil)
        {
            JoinChase(route);
            return;
        }

        var closing = MathF.Max(Driver.Speed - Vector3.Dot(route.PlayerVelocity, to) / MathF.Max(distance, 0.1f), 10);
        var seconds = distance / closing;
        var moving = route.PlayerSpeed >= SlowPlayerSpeed;

        // Past a certain point it's too late to change course, and that's the player's chance to dodge
        var target = seconds < HeadOnCommitSeconds && moving
            ? Driver.Position + Driver.Direction * 50
            : distance < HeadOnAimRange
                ? route.PlayerPosition + route.PlayerVelocity * seconds
                : PathAhead(MathF.Max(25, Driver.Speed * profile.LookaheadTime));

        // Someone who has stopped just gets blocked in, not hit
        var limit = moving ? HeadOnSpeedAt(heat) : MathF.Sqrt(2 * profile.Braking * MathF.Max(0, distance - HeadOnStopGap));

        var here = _path[_pathIndex];
        var next = _path[Math.Min(_pathIndex + 1, _path.Count - 1)];
        var length = MathF.Max((next - here).Length(), 0.1f);
        Driver.Drive(new DriveCommand(target, limit, 0, 0, 0, here.Y, (next.Y - here.Y) / length, 0), profile, dt);
    }

    private Vector3 PathAhead(float meters)
    {
        var travelled = 0f;
        for (var i = _pathIndex; i + 1 < _path.Count; i++)
        {
            travelled += (_path[i + 1] - _path[i]).Length();
            if (travelled >= meters) return _path[i + 1];
        }

        return _path[^1];
    }
}

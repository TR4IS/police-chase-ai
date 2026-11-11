using System.Numerics;

namespace CopBotPlugin;

/// <summary>
/// The way a player has driven, which is always a route a car can take. Recent driving is kept
/// as a smooth line that sits in the lane where there is one, so cops drive the road rather than
/// copying every twitch. It ends at the player's live position and carries on over the road ahead.
/// </summary>
public sealed class PlayerRoute
{
    private const float CrumbSpacing = 2;
    private const int MaxCrumbs = 4000;
    private const int SmoothRadius = 5;
    private const float InLaneDistance = 0.8f;
    private const float LeftLaneDistance = 2;
    private const float JumpDistance = 40;
    private const float JumpSeconds = 1.5f;
    private const float PredictMeters = 250;
    private const float SpeedFilterSeconds = 0.25f;
    private const float MinSpeedForDirection = 2;
    private const float PedalSeconds = 0.3f;
    private const float LaneJoinDistance = 8;

    private sealed class Crumb
    {
        public Vector3 Driven;
        public Vector3 Anchor;
        public Vector3 Smooth;
        public float Roll;
        public float SmoothRoll;
        public float Distance;
        public float Speed;
    }

    private readonly record struct FrontPoint(Vector3 Position, float Distance);

    private readonly IRoad _road;
    private readonly List<Crumb> _crumbs = new();
    private readonly List<FrontPoint> _front = new();
    private readonly List<Vector3> _lane = new();

    public PlayerRoute(IRoad road) => _road = road;

    public IRoad Road => _road;

    public Vector3 PlayerPosition { get; private set; }
    public Vector3 PlayerVelocity { get; private set; }
    public Vector3 PlayerDirection { get; private set; } = Vector3.UnitZ;
    public float PlayerSpeed { get; private set; }

    /// <summary>Distance along the route to the player's live position. Moves continuously.</summary>
    public float HeadDistance { get; private set; }

    /// <summary>How far back the route is remembered.</summary>
    public float StartDistance => _crumbs.Count > 0 ? _crumbs[0].Distance : 0;

    /// <summary>True when the road ahead of the player is known rather than guessed.</summary>
    public bool HasRoadAhead { get; private set; }

    /// <summary>Returns false when the player jumped somewhere else, which starts a new route.</summary>
    public bool Update(Vector3 position, Vector3 velocity, float roll, float dt)
    {
        var speed = velocity.Length();
        PlayerPosition = position;
        PlayerVelocity = velocity;
        PlayerSpeed += (speed - PlayerSpeed) * MathF.Min(1, dt / SpeedFilterSeconds);
        if (speed > MinSpeedForDirection) PlayerDirection = velocity / speed;

        // Further than a moment of lost connection at this speed could explain
        var jumped = false;
        if (_crumbs.Count > 0 && Flat(position - _crumbs[^1].Driven).Length() > JumpDistance + speed * JumpSeconds)
        {
            _crumbs.Clear();
            jumped = true;
        }

        if (_crumbs.Count == 0)
        {
            AddCrumb(position, roll, 0);
        }
        else
        {
            var moved = Flat(position - _crumbs[^1].Driven).Length();
            if (moved >= CrumbSpacing) AddCrumb(position, roll, _crumbs[^1].Distance + moved);
        }

        BuildFront();
        return !jumped;
    }

    public (Vector3 Position, float Roll) Sample(float distance)
    {
        var last = _crumbs[^1];
        if (distance >= last.Distance) return SampleFront(distance, last.SmoothRoll);
        if (distance <= _crumbs[0].Distance) return (_crumbs[0].Smooth, _crumbs[0].SmoothRoll);

        int low = 0, high = _crumbs.Count - 1;
        while (high - low > 1)
        {
            var mid = (low + high) / 2;
            if (_crumbs[mid].Distance <= distance) low = mid;
            else high = mid;
        }

        var a = _crumbs[low];
        var b = _crumbs[high];
        var t = (distance - a.Distance) / MathF.Max(b.Distance - a.Distance, 0.001f);
        return (Vector3.Lerp(a.Smooth, b.Smooth, t), a.SmoothRoll + (b.SmoothRoll - a.SmoothRoll) * t);
    }

    /// <summary>Direction of travel along the route at this distance.</summary>
    public Vector3 Tangent(float distance)
    {
        var along = Flat(Sample(distance + 2).Position - Sample(distance - 2).Position);
        return along.LengthSquared() > 0.01f ? Vector3.Normalize(along) : PlayerDirection;
    }

    /// <summary>Where a car is along the route, searching near where it was last time.</summary>
    public float Project(Vector3 position, float hint, out float offRoute)
    {
        var best = ProjectCrumbs(position, FirstCrumbAt(hint - 20), hint + 60, hint, out var bestSquared);
        ProjectFront(position, ref best, ref bestSquared, hint + 60);
        offRoute = MathF.Sqrt(bestSquared);
        return best;
    }

    /// <summary>The nearest part of the recent route, wherever it is. Used when a car has lost its place.</summary>
    public float ProjectNearest(Vector3 position, float searchBack, out float offRoute)
    {
        var best = ProjectCrumbs(position, FirstCrumbAt(HeadDistance - searchBack), float.MaxValue, HeadDistance, out var bestSquared);
        ProjectFront(position, ref best, ref bestSquared, float.MaxValue);
        offRoute = MathF.Sqrt(bestSquared);
        return best;
    }

    /// <summary>
    /// Fastest speed from which a driver can still brake for every bend ahead (v² = v_bend² + 2·a·d).
    /// A bend is good for what the tyres can do, or for what the player has just shown it can be taken at.
    /// </summary>
    public float CornerSpeedLimit(float from, float speed, float cornerGrip, float braking, float provenMargin)
    {
        // Judge each bend over a longer stretch the faster the car, as a driver does, so tiny
        // wiggles in the line don't read as bends. But look everywhere, or a corner gets missed.
        const float scanStep = 4;
        var span = MathF.Max(5, speed * 0.25f);
        var limitSquared = float.MaxValue;
        var pedalDistance = speed * PedalSeconds;
        var reach = speed * speed / (2 * braking) + pedalDistance + 25;

        for (var travelled = 0f; travelled < reach; travelled += scanStep)
        {
            var at = from + travelled;
            var curvature = Curvature(Sample(at - span).Position, Sample(at).Position, Sample(at + span).Position);
            var proven = ProvenSpeed(at, span) * provenMargin;
            var bendSquared = MathF.Max(cornerGrip / MathF.Max(curvature, 0.0005f), proven * proven);
            limitSquared = MathF.Min(limitSquared, bendSquared + 2 * braking * MathF.Max(0, travelled - pedalDistance));
        }

        return MathF.Sqrt(limitSquared);
    }

    // How fast this part of the road has just been shown to go. Where the player has been, that's how fast they
    // took it, unless they slowed right after, which may have been a crash. Around their car it's how fast they
    // are going now: the line there joins their position to the lane, and reads as a bend that isn't there.
    private float ProvenSpeed(float at, float span)
    {
        if (at + span < _crumbs[^1].Distance) return MathF.Min(_crumbs[FirstCrumbAt(at)].Speed, _crumbs[FirstCrumbAt(at + span)].Speed);
        return at - span < HeadDistance + LaneJoinDistance ? PlayerSpeed : 0;
    }

    private void AddCrumb(Vector3 position, float roll, float distance)
    {
        _crumbs.Add(new Crumb { Driven = position, Anchor = AnchorFor(position), Smooth = position, Roll = roll, SmoothRoll = roll, Distance = distance, Speed = PlayerSpeed });
        if (_crumbs.Count > MaxCrumbs) _crumbs.RemoveAt(0);

        // New information only changes the line around the end of the route
        var last = _crumbs.Count - 1;
        for (var i = Math.Max(0, last - SmoothRadius); i <= last; i++) SmoothCrumb(i, last);
    }

    // Pull the line to the middle of the lane only while the player is in that lane, fading out as
    // they leave it, so changing lanes stays a gentle drift and not a step sideways.
    private Vector3 AnchorFor(Vector3 position)
    {
        if (!_road.TryFindLane(position, PlayerDirection, out var lane)) return position;

        var side = new Vector3(PlayerDirection.Z, 0, -PlayerDirection.X);
        var offset = Vector3.Dot(Flat(lane - position), side);
        var pull = Math.Clamp((LeftLaneDistance - MathF.Abs(offset)) / (LeftLaneDistance - InLaneDistance), 0, 1);
        return position + side * (offset * pull);
    }

    private void SmoothCrumb(int index, int last)
    {
        var radius = Math.Min(SmoothRadius, Math.Min(index, last - index));
        var sum = Vector3.Zero;
        var roll = 0f;
        for (var i = index - radius; i <= index + radius; i++)
        {
            sum += _crumbs[i].Anchor;
            roll += _crumbs[i].Roll;
        }

        var count = 2 * radius + 1;
        _crumbs[index].Smooth = sum / count;
        _crumbs[index].SmoothRoll = roll / count;
    }

    private void BuildFront()
    {
        var last = _crumbs[^1];
        HeadDistance = last.Distance + Flat(PlayerPosition - last.Driven).Length();

        _front.Clear();
        _front.Add(new FrontPoint(last.Smooth, last.Distance));
        _front.Add(new FrontPoint(PlayerPosition, HeadDistance));

        HasRoadAhead = false;
        _lane.Clear();
        _road.FollowLane(PlayerPosition, PlayerDirection, PredictMeters, _lane);
        var previous = PlayerPosition;
        var distance = HeadDistance;
        foreach (var point in _lane)
        {
            // Join the lane a little ahead so the line doesn't kink at the player's car
            if (Vector3.Dot(point - PlayerPosition, PlayerDirection) < LaneJoinDistance) continue;

            distance += Flat(point - previous).Length();
            _front.Add(new FrontPoint(point, distance));
            previous = point;
            HasRoadAhead = true;
        }

        if (!HasRoadAhead)
        {
            _front.Add(new FrontPoint(PlayerPosition + PlayerDirection * PredictMeters, HeadDistance + PredictMeters));
        }
    }

    private (Vector3 Position, float Roll) SampleFront(float distance, float roll)
    {
        for (var i = 1; i < _front.Count; i++)
        {
            var a = _front[i - 1];
            var b = _front[i];
            if (distance > b.Distance && i < _front.Count - 1) continue;

            var t = (distance - a.Distance) / MathF.Max(b.Distance - a.Distance, 0.001f);
            return (Vector3.Lerp(a.Position, b.Position, Math.Clamp(t, 0, 1)), roll);
        }

        return (_front[^1].Position, roll);
    }

    private int FirstCrumbAt(float distance)
    {
        int low = 0, high = _crumbs.Count - 1;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_crumbs[mid].Distance < distance) low = mid + 1;
            else high = mid;
        }

        return Math.Max(0, low - 1);
    }

    private float ProjectCrumbs(Vector3 position, int first, float maxDistance, float fallback, out float bestSquared)
    {
        var best = fallback;
        bestSquared = float.MaxValue;
        for (var i = first; i + 1 < _crumbs.Count && _crumbs[i].Distance <= maxDistance; i++)
        {
            var a = _crumbs[i];
            var b = _crumbs[i + 1];
            Consider(position, a.Smooth, a.Distance, b.Smooth, b.Distance, ref best, ref bestSquared);
        }

        return best;
    }

    private void ProjectFront(Vector3 position, ref float best, ref float bestSquared, float maxDistance)
    {
        for (var i = 1; i < _front.Count && _front[i - 1].Distance <= maxDistance; i++)
        {
            var a = _front[i - 1];
            var b = _front[i];
            Consider(position, a.Position, a.Distance, b.Position, b.Distance, ref best, ref bestSquared);
        }
    }

    private static void Consider(Vector3 position, Vector3 a, float aDistance, Vector3 b, float bDistance, ref float best, ref float bestSquared)
    {
        var segment = Flat(b - a);
        var lengthSquared = segment.LengthSquared();
        var t = lengthSquared > 0.0001f ? Math.Clamp(Vector3.Dot(Flat(position - a), segment) / lengthSquared, 0, 1) : 0;
        var squared = Flat(position - (a + segment * t)).LengthSquared();
        if (squared < bestSquared)
        {
            bestSquared = squared;
            best = aDistance + (bDistance - aDistance) * t;
        }
    }

    private static float Curvature(Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = Flat(b - a);
        var bc = Flat(c - b);
        var ac = Flat(c - a);
        var area = MathF.Abs(ab.X * ac.Z - ab.Z * ac.X);
        var lengths = ab.Length() * bc.Length() * ac.Length();
        return lengths > 0.001f ? 2 * area / lengths : 0;
    }

    private static Vector3 Flat(Vector3 v) => v with { Y = 0 };
}

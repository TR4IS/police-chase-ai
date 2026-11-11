using System.Numerics;

namespace CopSim;

/// <summary>A human-ish driver on a fixed line: plans speed for bends, wanders a little, sometimes stops.</summary>
public sealed class SimPlayer
{
    private readonly List<Vector3> _line;
    private readonly float[] _along;
    private readonly float[] _maxSpeed;
    private readonly Random _random;
    private readonly float _braking;
    private readonly float _powerPerKg;
    private float _s;
    private float _time;

    public float Speed { get; private set; }
    public Vector3 Position { get; private set; }
    public Vector3 Velocity { get; private set; }
    public float StopAt = float.MaxValue;
    public float GoAt = float.MaxValue;
    public float Cap = float.MaxValue;
    public float SwerveEvery;
    public bool Finished => _s >= _along[^1] - 5;

    public SimPlayer(List<Vector3> line, Func<float, float> topSpeed, float grip, float braking, int seed, float powerPerKg = 0)
    {
        _line = line;
        _random = new Random(seed);
        _braking = braking;
        _powerPerKg = powerPerKg;
        _along = new float[line.Count];
        for (var i = 1; i < line.Count; i++) _along[i] = _along[i - 1] + Flat(line[i] - line[i - 1]).Length();

        // A driver judges a bend over a longer stretch the faster they are going, so small wiggles don't count
        _maxSpeed = new float[line.Count];
        for (var i = 0; i < line.Count; i++)
        {
            var speed = topSpeed(_along[i]);
            for (var pass = 0; pass < 3; pass++)
            {
                var span = MathF.Max(6, speed * 0.25f);
                var bend = MathF.Sqrt(grip / MathF.Max(Curvature(PointAt(_along[i] - span), line[i], PointAt(_along[i] + span)), 0.0005f));
                speed = MathF.Min(topSpeed(_along[i]), bend);
            }

            _maxSpeed[i] = speed;
        }

        for (var i = line.Count - 2; i >= 0; i--)
        {
            var ds = _along[i + 1] - _along[i];
            _maxSpeed[i] = MathF.Min(_maxSpeed[i], MathF.Sqrt(_maxSpeed[i + 1] * _maxSpeed[i + 1] + 2 * braking * ds));
        }

        Position = line[0];
    }

    public void Step(float dt)
    {
        _time += dt;
        var index = Index(_s);
        var want = _maxSpeed[index];
        if (_time >= StopAt && _time < GoAt) want = 0;
        want = MathF.Min(want, Cap);

        // With a power figure this is a real car: traction first, then power against drag
        var pull = _powerPerKg > 0 ? MathF.Min(8.5f, _powerPerKg / MathF.Max(Speed, 1)) - 0.0004f * Speed * Speed : 3.5f;
        var accel = Math.Clamp((want - Speed) * 6, -_braking - 0.5f, MathF.Max(pull, 0));
        Speed = MathF.Max(0, Speed + accel * dt);
        _s += Speed * dt;

        var previous = Position;
        var wander = (0.3f * MathF.Sin(_time * 1.3f) + 0.2f * MathF.Sin(_time * 3.1f + 1)) * MathF.Min(1, Speed / 8) + SwerveOffset();
        var i = Index(_s);
        var t = Math.Clamp((_s - _along[i]) / MathF.Max(_along[Math.Min(i + 1, _line.Count - 1)] - _along[i], 0.001f), 0, 1);
        var p = Vector3.Lerp(_line[i], _line[Math.Min(i + 1, _line.Count - 1)], t);
        var tangent = Vector3.Normalize(Flat(_line[Math.Min(i + 1, _line.Count - 1)] - _line[Math.Max(i, 0)]) + new Vector3(0, 0, 1e-5f));
        Position = p + new Vector3(tangent.Z, 0, -tangent.X) * wander;
        Velocity = dt > 0 ? (Position - previous) / dt : Vector3.Zero;
    }

    // Now and then a sudden jink across a lane and back
    private float SwerveOffset()
    {
        if (SwerveEvery <= 0) return 0;

        var phase = _time % SwerveEvery;
        var f = phase < 0.8f ? phase / 0.8f : phase < 2 ? 1 : phase < 2.8f ? (2.8f - phase) / 0.8f : 0;
        return 3.5f * f * f * (3 - 2 * f);
    }

    private Vector3 PointAt(float s) => _line[Index(s)];

    private int Index(float s)
    {
        var i = Array.BinarySearch(_along, s);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, _line.Count - 1);
    }

    private static Vector3 Flat(Vector3 v) => v with { Y = 0 };

    private static float Curvature(Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = Flat(b - a);
        var bc = Flat(c - b);
        var ac = Flat(c - a);
        var area = MathF.Abs(ab.X * ac.Z - ab.Z * ac.X);
        var lengths = ab.Length() * bc.Length() * ac.Length();
        return lengths > 0.001f ? 2 * area / lengths : 0;
    }
}

public static class Lines
{
    /// <summary>A street map: straights joined by turns of the given radii, left and right.</summary>
    public static List<Vector3> Streets(params (float Straight, float Radius, float Degrees)[] parts)
    {
        var line = new List<Vector3>();
        var p = Vector3.Zero;
        var heading = 0f;
        line.Add(p);
        foreach (var (straight, radius, degrees) in parts)
        {
            for (var s = 0f; s < straight; s += 2)
            {
                p += new Vector3(MathF.Sin(heading), 0, MathF.Cos(heading)) * 2;
                line.Add(p);
            }

            if (radius <= 0) continue;
            var arc = MathF.Abs(degrees) * MathF.PI / 180 * radius;
            var turnPerStep = 2 / radius * MathF.Sign(degrees);
            for (var s = 0f; s < arc; s += 2)
            {
                heading += turnPerStep;
                p += new Vector3(MathF.Sin(heading), 0, MathF.Cos(heading)) * 2;
                line.Add(p);
            }
        }
        return line;
    }

    /// <summary>A long drive down a real lane, changing lanes now and then.</summary>
    public static List<Vector3> RealLane(CacheRoad road, int start, float meters, int seed)
    {
        var random = new Random(seed);
        var line = new List<Vector3>();
        var id = start;
        var travelled = 0f;
        var nextChange = 300f;
        int target = -1;
        var changeAt = 0f;
        const float changeLength = 70;
        while (travelled < meters)
        {
            var p = road.Pos[id];
            if (target >= 0)
            {
                var neighbour = Neighbour(road, id, target);
                if (neighbour < 0)
                {
                    target = -1;
                }
                else
                {
                    var f = Math.Clamp((travelled - changeAt) / changeLength, 0, 1);
                    f = f * f * (3 - 2 * f);
                    p = Vector3.Lerp(p, road.Pos[neighbour], f);
                    if (f >= 1)
                    {
                        id = neighbour;
                        target = -1;
                        nextChange = travelled + 200 + random.NextSingle() * 400;
                    }
                }
            }
            else if (travelled > nextChange)
            {
                var side = random.Next(2);
                if (Neighbour(road, id, side) >= 0)
                {
                    target = side;
                    changeAt = travelled;
                }
                else
                {
                    nextChange = travelled + 100;
                }
            }

            line.Add(p);
            travelled += road.Len[id];
            id = road.Next[id];
        }
        return line;
    }

    private static int Neighbour(CacheRoad road, int id, int side)
    {
        var n = side == 0 ? road.Left[id] : road.Right[id];
        return n >= 0 && Vector3.Dot(road.Forward(n), road.Forward(id)) > 0 ? n : -1;
    }
}

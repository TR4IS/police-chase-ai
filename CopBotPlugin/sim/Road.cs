using System.Numerics;
using CopBotPlugin;

namespace CopSim;

/// <summary>The real track lanes, read from the server's cache file. Same rules as SplineRoad.</summary>
public sealed class CacheRoad : IRoad
{
    private const float Cell = 25;
    private const float MaxDistanceToLane = 3;

    public readonly Vector3[] Pos;
    public readonly float[] Len;
    public readonly int[] Next, Left, Right;
    private readonly Dictionary<(int, int), List<int>> _grid = new();

    public CacheRoad(string path)
    {
        var data = File.ReadAllBytes(path);
        var count = BitConverter.ToInt32(data, 4);
        Pos = new Vector3[count];
        Len = new float[count];
        Next = new int[count];
        Left = new int[count];
        Right = new int[count];
        for (var i = 0; i < count; i++)
        {
            var o = 16 + i * 56;
            Pos[i] = new Vector3(BitConverter.ToSingle(data, o + 4), BitConverter.ToSingle(data, o + 8), BitConverter.ToSingle(data, o + 12));
            Len[i] = BitConverter.ToSingle(data, o + 24);
            Next[i] = BitConverter.ToInt32(data, o + 40);
            Left[i] = BitConverter.ToInt32(data, o + 44);
            Right[i] = BitConverter.ToInt32(data, o + 48);
            var key = Key(Pos[i]);
            if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<int>();
            list.Add(i);
        }
    }

    private static (int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Z / Cell));

    private int Nearest(Vector3 p, out float squared)
    {
        var (cx, cz) = Key(p);
        var best = -1;
        squared = float.MaxValue;
        for (var x = cx - 1; x <= cx + 1; x++)
        for (var z = cz - 1; z <= cz + 1; z++)
        {
            if (!_grid.TryGetValue((x, z), out var list)) continue;
            foreach (var i in list)
            {
                var d = Vector3.DistanceSquared(Pos[i], p);
                if (d < squared)
                {
                    squared = d;
                    best = i;
                }
            }
        }
        return best;
    }

    public Vector3 Forward(int i) => Pos[Next[i]] - Pos[i];

    private bool TryPoint(Vector3 p, Vector3 dir, out int id)
    {
        id = Nearest(p, out var d);
        return id >= 0 && d <= MaxDistanceToLane * MaxDistanceToLane && Vector3.Dot(Forward(id), dir) > 0;
    }

    public bool TryFindLane(Vector3 position, Vector3 direction, out Vector3 laneCentre)
    {
        laneCentre = default;
        if (!TryPoint(position, direction, out var id)) return false;
        laneCentre = Pos[id];
        return true;
    }

    public bool TryFindAdjacentLane(Vector3 position, Vector3 direction, Vector3 preferredSide, out Vector3 offset)
    {
        offset = default;
        if (!TryPoint(position, direction, out var id)) return false;
        var found = false;
        foreach (var n in new[] { Left[id], Right[id] })
        {
            if (n < 0 || Vector3.Dot(Forward(n), direction) <= 0) continue;
            var candidate = Pos[n] - Pos[id];
            if (!found || Vector3.Dot(candidate, preferredSide) > 0)
            {
                offset = candidate;
                found = true;
            }
        }
        return found;
    }

    public void FollowLane(Vector3 position, Vector3 direction, float meters, List<Vector3> points)
    {
        if (!TryPoint(position, direction, out var id)) return;
        var travelled = 0f;
        var step = 0;
        while (travelled < meters)
        {
            travelled += Len[id];
            id = Next[id];
            if (++step % 3 == 0) points.Add(Pos[id]);
        }
    }
}

/// <summary>Streets with no lane data: the driver only has the player's line to go on.</summary>
public sealed class NoRoad : IRoad
{
    public bool TryFindLane(Vector3 position, Vector3 direction, out Vector3 laneCentre)
    {
        laneCentre = default;
        return false;
    }

    public bool TryFindAdjacentLane(Vector3 position, Vector3 direction, Vector3 preferredSide, out Vector3 offset)
    {
        offset = default;
        return false;
    }

    public void FollowLane(Vector3 position, Vector3 direction, float meters, List<Vector3> points)
    {
    }
}

using System.Numerics;

namespace CopBotPlugin;

/// <summary>Where cops can lie in wait on the road ahead of the player, worked out from the track's lanes.</summary>
public static class Ambush
{
    private const float MinAhead = 330;
    private const float MaxAhead = 650;
    private const float AheadSeconds = 6;
    private const float SearchFurther = 250;
    private const float MinRunUp = 90;
    private const float RunUpSeconds = 2.5f;
    private const float RunUpCorridor = 5;
    private const float MinStraightDistance = 220;
    private const float BroadsideAngle = 1.2f;
    private const float StaggerDistance = 28;
    private const float SingleLaneOffset = 1.2f;
    private const int MaxLanesEachSide = 3;

    public readonly record struct Placement(Vector3 Position, float Heading);

    /// <summary>
    /// A place on the player's lane far enough ahead to be out of the way and with a clear view of it on
    /// the way up, so the player has a fair chance to see it. Returns the lane up to that place.
    /// </summary>
    public static bool TryFindSpot(IRoad road, Vector3 position, Vector3 direction, float speed, out List<Vector3> path)
    {
        var wanted = Math.Clamp(speed * AheadSeconds, MinAhead, MaxAhead);
        var lane = new List<Vector3>();
        road.FollowLane(position, direction, wanted + SearchFurther, lane);
        path = lane;

        var runUp = MathF.Max(MinRunUp, speed * RunUpSeconds);
        var along = new float[lane.Count];
        for (var i = 1; i < lane.Count; i++) along[i] = along[i - 1] + Flat(lane[i] - lane[i - 1]).Length();

        for (var i = 1; i < lane.Count; i++)
        {
            if (along[i] < wanted || Flat(lane[i] - position).Length() < MinStraightDistance) continue;
            if (!IsStraight(lane, along, i, runUp)) continue;

            path = lane.GetRange(0, i + 1);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Cars parked across the road at the end of the path. The player's own lane is always blocked but there
    /// is always a way through for someone who has seen it in time: a free lane, or a slalom on a narrow road.
    /// </summary>
    public static List<Placement> Roadblock(IRoad road, List<Vector3> path, int maxCars, bool mirror)
    {
        var at = path[^1];
        var along = Along(path);
        var right = new Vector3(along.Z, 0, -along.X);
        var heading = CopDriver.HeadingOf(along) + (mirror ? -BroadsideAngle : BroadsideAngle);

        var left = LanesTowards(road, at, along, -right);
        var other = LanesTowards(road, at, along, right);

        var cars = new List<Placement>();
        if (left.Count + other.Count == 0)
        {
            cars.Add(new Placement(at + right * (mirror ? -SingleLaneOffset : SingleLaneOffset), heading));
            return cars;
        }

        cars.Add(new Placement(at, heading));
        if (left.Count + other.Count == 1)
        {
            var neighbour = left.Count == 1 ? left[0] : other[0];
            cars.Add(new Placement(neighbour + along * StaggerDistance, heading));
        }
        else
        {
            // All the lanes but the furthest one on one side
            var free = left.Count >= other.Count ? left : other;
            free.RemoveAt(free.Count - 1);
            foreach (var lane in left.Concat(other)) cars.Add(new Placement(lane, heading));
        }

        return cars.Take(maxCars).ToList();
    }

    /// <summary>Facing back down the path, towards the player.</summary>
    public static Placement HeadOn(List<Vector3> path) => new(path[^1], CopDriver.HeadingOf(-Along(path)));

    /// <summary>Which way the road runs at the end of the path.</summary>
    public static Vector3 Along(List<Vector3> path)
    {
        var from = path[Math.Max(0, path.Count - 4)];
        var along = Flat(path[^1] - from);
        return along.LengthSquared() > 0.01f ? Vector3.Normalize(along) : Vector3.UnitZ;
    }

    private static List<Vector3> LanesTowards(IRoad road, Vector3 from, Vector3 along, Vector3 side)
    {
        var lanes = new List<Vector3>();
        var at = from;
        while (lanes.Count < MaxLanesEachSide
               && road.TryFindAdjacentLane(at, along, side, out var offset)
               && Vector3.Dot(offset, side) > 0)
        {
            at += offset;
            lanes.Add(at);
        }

        return lanes;
    }

    private static bool IsStraight(List<Vector3> lane, float[] along, int end, float length)
    {
        var from = end;
        while (from > 0 && along[end] - along[from] < length) from--;

        var chord = Flat(lane[end] - lane[from]);
        var chordLength = chord.Length();
        if (chordLength < 1) return false;

        for (var i = from + 1; i < end; i++)
        {
            var point = Flat(lane[i] - lane[from]);
            if (MathF.Abs(point.X * chord.Z - point.Z * chord.X) / chordLength > RunUpCorridor) return false;
        }

        return true;
    }

    private static Vector3 Flat(Vector3 v) => v with { Y = 0 };
}

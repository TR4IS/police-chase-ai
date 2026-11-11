using System.Numerics;
using AssettoServer.Server.Ai.Splines;

namespace CopBotPlugin;

/// <summary>The road as described by the track's AI lanes.</summary>
public sealed class SplineRoad : IRoad
{
    private const float MaxDistanceToLane = 3;
    private const int PointsPerPrediction = 3;

    private readonly AiSpline _spline;

    public SplineRoad(AiSpline spline) => _spline = spline;

    public bool TryFindLane(Vector3 position, Vector3 direction, out Vector3 laneCentre)
    {
        laneCentre = default;
        if (!TryFindPoint(position, direction, out var pointId)) return false;

        laneCentre = _spline.Points[pointId].Position;
        return true;
    }

    public bool TryFindAdjacentLane(Vector3 position, Vector3 direction, Vector3 preferredSide, out Vector3 offset)
    {
        offset = default;
        if (!TryFindPoint(position, direction, out var pointId)) return false;

        var points = _spline.Points;
        var here = points[pointId].Position;
        var found = false;

        foreach (var neighbour in new[] { points[pointId].LeftId, points[pointId].RightId })
        {
            if (neighbour < 0 || !GoesThisWay(neighbour, direction)) continue;

            var candidate = points[neighbour].Position - here;
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
        if (!TryFindPoint(position, direction, out var pointId)) return;

        var spline = _spline.Points;
        var travelled = 0f;
        var step = 0;
        while (travelled < meters)
        {
            var next = spline[pointId].NextId;
            if (next < 0) return;

            travelled += spline[pointId].Length;
            pointId = next;
            if (++step % PointsPerPrediction == 0) points.Add(spline[pointId].Position);
        }
    }

    private bool TryFindPoint(Vector3 position, Vector3 direction, out int pointId)
    {
        var (nearest, distanceSquared) = _spline.WorldToSpline(position);
        pointId = nearest;
        return nearest >= 0
               && distanceSquared <= MaxDistanceToLane * MaxDistanceToLane
               && GoesThisWay(nearest, direction);
    }

    private bool GoesThisWay(int pointId, Vector3 direction) =>
        Vector3.Dot(_spline.Operations.GetForwardVector(pointId), direction) > 0;
}

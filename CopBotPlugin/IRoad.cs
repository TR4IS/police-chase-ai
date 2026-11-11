using System.Numerics;

namespace CopBotPlugin;

/// <summary>What a driver can see of the road around them.</summary>
public interface IRoad
{
    /// <summary>Centre of the lane at this position going the same way, if the position is in one.</summary>
    bool TryFindLane(Vector3 position, Vector3 direction, out Vector3 laneCentre);

    /// <summary>Offset from this lane to a neighbouring lane going the same way, on the preferred side if possible.</summary>
    bool TryFindAdjacentLane(Vector3 position, Vector3 direction, Vector3 preferredSide, out Vector3 offset);

    /// <summary>The lane ahead of this position, as points in driving order.</summary>
    void FollowLane(Vector3 position, Vector3 direction, float meters, List<Vector3> points);
}

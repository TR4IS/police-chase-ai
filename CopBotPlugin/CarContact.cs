using System.Numerics;

namespace CopBotPlugin;

/// <summary>
/// Two cars touching, as two boxes with mass. A hit pushes a cop by what the momentum says and
/// turns it by how far from its middle the hit landed, so a clip on the rear corner spins it.
/// </summary>
public static class CarContact
{
    private const float HalfLength = 2.45f;
    private const float HalfWidth = 0.95f;
    private const float PlayerMass = 1210;
    private const float CopMass = 1800;
    private const float Bounce = 0.2f;
    private const float InertiaPerMass = 2.3f;
    private const float MaxYawChange = 3;

    /// <summary>Normal points from the player to the cop. Penetration is negative when they are apart.</summary>
    public readonly record struct Touch(Vector3 Normal, float Penetration, Vector3 Lever);

    public static Touch Measure(Vector3 cop, Vector3 copDirection, Vector3 player, Vector3 playerDirection)
    {
        var between = cop - player with { Y = cop.Y };
        var copSide = new Vector3(copDirection.Z, 0, -copDirection.X);
        var playerSide = new Vector3(playerDirection.Z, 0, -playerDirection.X);

        var normal = copDirection;
        var least = float.MaxValue;
        foreach (var axis in new[] { copDirection, copSide, playerDirection, playerSide })
        {
            var along = Vector3.Dot(between, axis);
            var reach = Extent(copDirection, copSide, axis) + Extent(playerDirection, playerSide, axis);
            var overlap = reach - MathF.Abs(along);
            if (overlap >= least) continue;

            least = overlap;
            normal = along >= 0 ? axis : -axis;
        }

        // Where on the cop's box the player is
        var lever = copDirection * Math.Clamp(-Vector3.Dot(between, copDirection), -HalfLength, HalfLength)
                    + copSide * Math.Clamp(-Vector3.Dot(between, copSide), -HalfWidth, HalfWidth);
        return new Touch(normal, least, lever);
    }

    /// <summary>What a hit at this closing speed does to the cop's speed (m/s) and rate of turn (rad/s).</summary>
    public static (Vector3 Kick, float Yaw) Impulse(in Touch touch, float closingSpeed)
    {
        var kick = touch.Normal * ((1 + Bounce) * PlayerMass / (PlayerMass + CopMass) * closingSpeed);
        var yaw = (touch.Lever.X * kick.Z - touch.Lever.Z * kick.X) / InertiaPerMass;
        return (kick, Math.Clamp(yaw, -MaxYawChange, MaxYawChange));
    }

    private static float Extent(Vector3 direction, Vector3 side, Vector3 axis) =>
        HalfLength * MathF.Abs(Vector3.Dot(direction, axis)) + HalfWidth * MathF.Abs(Vector3.Dot(side, axis));
}

using AssettoServer.Shared.Model;
using Serilog;

namespace CopBotPlugin;

/// <summary>
/// Learns how the game encodes steering by watching real players: how far their car turns against
/// the numbers it reports. Until it has seen enough, a cop's wheels simply stay straight.
/// </summary>
public sealed class SteeringCalibration
{
    private const byte Straight = 127;
    private const float MinSpeed = 8;
    private const float MinDegrees = 0.4f;
    private const float MaxDegrees = 30;
    private const int SamplesNeeded = 150;

    private float _wheelSum;
    private float _steerSum;
    private float _degreesSum;
    private int _samples;
    private float _wheelPerDegree;
    private float _steerPerDegree;

    public void Observe(CarStatus status, float lastHeading, float dt)
    {
        var speed = status.Velocity.Length();
        if (speed < MinSpeed || dt <= 0) return;

        var turned = MathF.IEEERemainder(status.Rotation.X - lastHeading, 2 * MathF.PI);
        var degrees = MathF.Atan(CopDriver.Wheelbase * turned / dt / speed) * 180 / MathF.PI;
        if (MathF.Abs(degrees) < MinDegrees || MathF.Abs(degrees) > MaxDegrees) return;

        _wheelSum += degrees * (status.WheelAngle - Straight);
        _steerSum += degrees * (status.SteerAngle - Straight);
        _degreesSum += degrees * degrees;

        if (++_samples != SamplesNeeded) return;

        _wheelPerDegree = _wheelSum / _degreesSum;
        _steerPerDegree = _steerSum / _degreesSum;
        Log.Information("[CopBot] Steering learned: {Wheel:F2} wheel and {Steer:F2} steer units per degree", _wheelPerDegree, _steerPerDegree);
    }

    public byte EncodeWheel(float radians) => Encode(radians, _wheelPerDegree);

    public byte EncodeSteer(float radians) => Encode(radians, _steerPerDegree);

    private static byte Encode(float radians, float unitsPerDegree) =>
        (byte)Math.Clamp(Straight + radians * 180 / MathF.PI * unitsPerDegree, 0, 254);
}

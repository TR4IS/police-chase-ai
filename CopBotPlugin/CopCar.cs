using System.Numerics;
using AssettoServer.Server;
using AssettoServer.Server.Ai;
using AssettoServer.Shared.Network.Packets.Outgoing;

namespace CopBotPlugin;

/// <summary>
/// A traffic slot taken over for a chase. What the driver does each tick is written into the
/// car's status, which is what every player receives.
/// </summary>
public sealed class CopCar
{
    // The Crown Vic's own gearbox and engine (drivetrain.ini, engine.ini), with two taller ratios on
    // top so the engine note still sounds right at speeds the real car never saw
    private static readonly float[] GearRatios = [2.85f, 1.65f, 1.0f, 0.75f, 0.55f, 0.42f];
    private const int RealGears = 4;
    private const float FinalDrive = 5.45f;
    private const float IdleRpm = 850;
    private const float MaxRpm = 6500;
    private const float ShiftUpRpm = 5600;
    private const float CruiseShiftUpRpm = 3800;
    private const float ShiftDownRpm = 2000;
    private const float ReverseGearByte = 0;
    private const float FirstGearByte = 2;
    private const float RpmSeconds = 0.12f;
    private const long FlashMs = 300;

    private int _gear;
    private float _rpm = IdleRpm;

    public CopCar(EntryCar car, AiState state, CopUnit unit)
    {
        Car = car;
        State = state;
        Unit = unit;
    }

    public EntryCar Car { get; }
    public AiState State { get; }
    public CopUnit Unit { get; }
    public Vector3 Position => State.Status.Position;

    public void Update(PlayerRoute route, List<CopUnit> units, float heat, long now, float dt, SteeringCalibration steering)
    {
        State.SpawnProtectionEnds = long.MaxValue;  // Normal traffic must not recycle a chasing cop

        Unit.Update(route, units, heat, now, dt);
        WriteStatus(steering, now, dt);
    }

    /// <summary>Back to the pool of waiting cars.</summary>
    public void Release() => State.SpawnProtectionEnds = 0;

    private void WriteStatus(SteeringCalibration steering, long now, float dt)
    {
        var driver = Unit.Driver;
        var status = State.Status;

        status.Timestamp = now;
        status.Position = driver.Position;
        status.Rotation = new Vector3(driver.Heading, driver.Pitch, driver.Roll);
        status.Velocity = driver.Velocity;
        status.WheelAngle = steering.EncodeWheel(driver.WheelAngle);
        status.SteerAngle = steering.EncodeSteer(driver.WheelAngle);

        var speed = MathF.Abs(driver.Speed);
        var tyre = speed / (MathF.PI * Car.TyreDiameterMeters) * 6;
        var encodedTyre = (byte)(Math.Clamp(MathF.Round(MathF.Log10(tyre + 1) * 20), -100, 154) + 100);
        for (var i = 0; i < 4; i++) status.TyreAngularSpeed[i] = encodedTyre;

        UpdateEngine(driver, speed, dt);
        status.EngineRpm = (ushort)_rpm;
        status.Gear = (byte)(driver.IsReversing ? ReverseGearByte : FirstGearByte + Math.Min(_gear, RealGears - 1));
        status.Gas = (byte)(driver.Throttle * 255);

        // The horn input drives the siren and strobes on this car, high beams flash alongside.
        // A wreck has its siren dead and its hazards on.
        status.StatusFlag = driver.IsWrecked
            ? CarStatusFlags.HazardsOn
            : CarStatusFlags.LightsOn
              | CarStatusFlags.Horn
              | (now / FlashMs % 2 == 0 ? CarStatusFlags.HighBeamsOff : 0)
              | (driver.IsBraking ? CarStatusFlags.BrakeLightsOn : 0);
    }

    // Revs follow the wheels through a real gearbox, so the engine note rises and shifts like a car
    private void UpdateEngine(CopDriver driver, float speed, float dt)
    {
        float RpmIn(int gear) => speed / (MathF.PI * Car.TyreDiameterMeters) * 60 * GearRatios[gear] * FinalDrive;

        var shiftUp = driver.Throttle > 0.5f ? ShiftUpRpm : CruiseShiftUpRpm;
        if (_gear < GearRatios.Length - 1 && RpmIn(_gear) > shiftUp) _gear++;
        else if (_gear > 0 && RpmIn(_gear) < ShiftDownRpm) _gear--;

        var target = Math.Clamp(RpmIn(_gear), IdleRpm, MaxRpm);
        _rpm += (target - _rpm) * MathF.Min(1, dt / RpmSeconds);
    }
}

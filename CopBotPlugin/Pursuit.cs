using System.Numerics;
using AssettoServer.Network.ClientMessages;
using AssettoServer.Server;
using Serilog;

namespace CopBotPlugin;

[OnlineEvent(Key = "copBotState")]
public class CopBotState : OnlineEvent<CopBotState>
{
    [OnlineEventField(Name = "phase")] public byte Phase;
    [OnlineEventField(Name = "stars")] public byte Stars;
    [OnlineEventField(Name = "cops")] public byte Cops;
    [OnlineEventField(Name = "down")] public byte Down;
    [OnlineEventField(Name = "bust")] public byte BustPercent;
    [OnlineEventField(Name = "escape")] public byte EscapePercent;
    [OnlineEventField(Name = "alert")] public byte Alert;
    [OnlineEventField(Name = "alertId")] public byte AlertId;
    [OnlineEventField(Name = "nearest")] public ushort NearestMeters;
}

/// <summary>One player's trouble with the law: heat, stars, the cops on them, and how it ends.</summary>
public sealed class Pursuit
{
    private const float Kmh = 1 / 3.6f;

    private const byte PhaseIdle = 0, PhaseChase = 1, PhaseBusted = 2, PhaseEscaped = 3;
    private const byte AlertRoadblock = 1, AlertHeadOn = 2, AlertUnitDown = 3;

    private const float SpeedingFrom = 80 * Kmh;
    private const float HeatToStart = 3;
    private const float MaxHeat = 10;

    private const int MaxStars = 5;
    private const long StarEveryMs = 30_000;
    private const float RecklessSpeed = 160 * Kmh;
    private const float RecklessRate = 1.25f;
    private const long HitStarCooldownMs = 10_000;
    private const int RamsFromStars = 3;

    private const float MinRouteToJoin = 100;
    private const long JoinSpacingMs = 1500;
    private const long BackupBaseMs = 14_000;
    private const long BackupPerStarMs = 2000;
    private const float LostCopFarEnough = 150;
    private const float TooFarBehind = 450;
    private const float WreckGoneDistance = 150;
    private const long WreckStaysMs = 25_000;

    private const int RoadblocksFromStars = 3;
    private const int HeadOnFromStars = 4;
    private const int MaxRoadblockCars = 3;
    private const long AmbushFirstMs = 8_000;
    private const long AmbushRetryMs = 3_000;
    private const float AmbushMinSpeed = 20;

    private const float BustDistance = 25;
    private const float BustSpeed = 30 * Kmh;
    private const float BustSeconds = 5;
    private const float BustDrainSeconds = 2.5f;
    private const float EvadeSeconds = 10;
    private const float EvadeDrainSeconds = 3;
    private const long BustScreenMs = 4_000;
    private const long EscapedScreenMs = 3_000;
    private const long CooldownMs = 12_000;
    private const long HudIntervalMs = 300;
    private const long LogEveryMs = 5_000;

    private readonly EntryCar _player;
    private readonly CopBot _copBot;
    private readonly SteeringCalibration _steering;
    private readonly PlayerRoute _route;
    private readonly Random _random = new();
    private readonly List<CopCar> _cars = new();
    private readonly List<CopUnit> _units = new();

    private byte _phase;
    private long _phaseUntil;
    private float _heat;
    private int _stars;
    private float _starProgress;
    private long _lastHitStar;
    private long _cooldownUntil;
    private float _bust;
    private float _evade;
    private float _nearest;
    private int _down;
    private byte _alert;
    private byte _alertId;
    private int _rolesForStars;
    private int _rolesForCount;
    private long _nextCop;
    private long _nextAmbush;
    private bool _nextAmbushIsHeadOn;
    private float _lastHeading;
    private long _nextHud;
    private long _nextLog;
    private long _startedAt;
    private float _topSpeed;
    private byte _lastHudPhase = byte.MaxValue;
    private int _lastHudStars;
    private byte _lastHudAlertId;

    public Pursuit(EntryCar player, CopBot copBot, IRoad road, SteeringCalibration steering)
    {
        _player = player;
        _copBot = copBot;
        _steering = steering;
        _route = new PlayerRoute(road);
    }

    public bool Uses(EntryCar car) => _cars.Any(cop => cop.Car == car);

    public void Update(long now, float dt)
    {
        var status = _player.Status;
        _steering.Observe(status, _lastHeading, dt);
        _lastHeading = status.Rotation.X;

        if (!_route.Update(status.Position, status.Velocity, status.Rotation.Z, dt) && _phase == PhaseChase)
        {
            // Teleported to the pits or similar, the route no longer leads to the player
            End();
            _phase = PhaseIdle;
            _stars = 0;
        }

        switch (_phase)
        {
            case PhaseIdle when now >= _cooldownUntil:
                UpdateHeat(now, dt);
                break;
            case PhaseChase:
                UpdateChase(now, dt);
                break;
            case PhaseBusted when now >= _phaseUntil:
                _copBot.SendToPits(_player);
                Reset(now);
                break;
            case PhaseEscaped when now >= _phaseUntil:
                Reset(now);
                break;
        }

        SendHud(now);
    }

    /// <summary>The player hit something. If it was a cop chasing them, both sides feel it.</summary>
    public void OnCollision(EntryCar target, float impactKmh, long now)
    {
        if (_phase != PhaseChase) return;

        var cop = _cars.FirstOrDefault(c => c.Car == target);
        if (cop == null || cop.Unit.IsWrecked) return;

        cop.Unit.OnHit(_player.Status.Position, _route.PlayerDirection, impactKmh, now);

        if (cop.Unit.IsWrecked)
        {
            _down++;
            _nextCop = now + BackupBaseMs - _stars * BackupPerStarMs;
            Alert(AlertUnitDown);
            Log.Information("[CopBot] {Cop} wrecked by {Name} at {Impact:F0} km/h", target.AiName, _player.Client?.Name, impactKmh);
        }

        // Hitting police is a serious offence, a ramming cop hitting you is not
        if ((_stars < RamsFromStars || cop.Unit.IsWrecked) && now - _lastHitStar > HitStarCooldownMs)
        {
            _lastHitStar = now;
            AddStar(now);
        }
    }

    /// <summary>Sends all the cops away.</summary>
    public void End()
    {
        foreach (var cop in _cars) cop.Release();
        _cars.Clear();
        _units.Clear();
        _heat = 0;
    }

    private void Reset(long now)
    {
        _phase = PhaseIdle;
        _stars = 0;
        _cooldownUntil = now + CooldownMs;
    }

    private void UpdateHeat(long now, float dt)
    {
        var speeding = _player.Status.Velocity.Length() >= SpeedingFrom;
        _heat = Math.Clamp(_heat + (speeding ? dt : -dt / 4), 0, MaxHeat);
        if (_heat < HeatToStart) return;

        _phase = PhaseChase;
        _stars = 1;
        _starProgress = 0;
        _bust = 0;
        _evade = 0;
        _down = 0;
        _topSpeed = 0;
        _startedAt = now;
        _nextCop = now;
        _nextAmbush = now;
        _rolesForCount = 0;
        Log.Information("[CopBot] Pursuit started on {Name}", _player.Client?.Name);
    }

    private void AddStar(long now)
    {
        if (_stars >= MaxStars) return;

        if (_stars == RoadblocksFromStars - 1) _nextAmbush = now + AmbushFirstMs;
        _stars++;
        _starProgress = 0;
        Log.Information("[CopBot] {Name} is now at {Stars} stars", _player.Client?.Name, _stars);
    }

    private void Alert(byte alert)
    {
        _alert = alert;
        _alertId++;
    }

    private void UpdateChase(long now, float dt)
    {
        var speed = _player.Status.Velocity.Length();
        _topSpeed = MathF.Max(_topSpeed, speed);

        // Time on the run raises the stars, faster when driving recklessly
        _starProgress += dt * 1000 * (speed >= RecklessSpeed ? RecklessRate : 1);
        if (_starProgress >= StarEveryMs) AddStar(now);

        var chasers = _units.Count(unit => unit.IsChasing);
        if (chasers < CopUnit.UnitsForStars(_stars) && now >= _nextCop) AddCop(chasers, now);
        else if (chasers > CopUnit.UnitsForStars(_stars)) SendAwayExtraCop();
        if (_stars >= RoadblocksFromStars && now >= _nextAmbush) AddAmbush(now);
        AssignRoles();

        var heat = DriverProfile.HeatForStars(_stars);
        var seen = false;
        _nearest = float.MaxValue;
        foreach (var cop in _cars.ToArray())
        {
            // A player can join in a car from the traffic pool
            if (!cop.Car.AiControlled)
            {
                Remove(cop);
                continue;
            }

            if (!cop.State.Initialized)
            {
                var (pointId, valid) = _copBot.SplinePointNear(cop.Position);
                if (valid) cop.State.Teleport(pointId);
            }

            cop.Update(_route, _units, heat, now, dt, _steering);
            var distance = Vector3.Distance(cop.Position, _player.Status.Position);

            if (cop.Unit.Incident != null)
            {
                Log.Information("[CopBot] {Cop} ({Role}, {Distance:F0}m away): {Incident}", cop.Car.AiName, cop.Unit.Role, distance, cop.Unit.Incident);
                cop.Unit.Incident = null;
            }

            if (cop.Unit.IsWrecked)
            {
                if (distance > WreckGoneDistance || now - cop.Unit.WreckedAt > WreckStaysMs) Remove(cop);
                continue;
            }

            if (cop.Unit.IsLost && distance > LostCopFarEnough)
            {
                Log.Information("[CopBot] {Cop} ({Role}) lost {Name}", cop.Car.AiName, cop.Unit.Role, _player.Client?.Name);
                Remove(cop);
                continue;
            }

            // Spun out and left far behind: radio for a fresh unit instead of watching it try to catch up
            if (cop.Unit.IsChasing && _route.HeadDistance - cop.Unit.Progress > TooFarBehind && !cop.Unit.Sees(_route))
            {
                Remove(cop);
                _nextCop = Math.Max(_nextCop, now + BackupBaseMs - _stars * BackupPerStarMs);
                continue;
            }

            // A cop spinning across the road isn't arresting anybody
            if (!cop.Unit.Driver.IsSkidding) _nearest = MathF.Min(_nearest, distance);
            seen |= cop.Unit.Sees(_route);
        }

        CheckEnding(speed, seen, now, dt);
        LogChase(speed, now);
    }

    private void Remove(CopCar cop)
    {
        cop.Release();
        _cars.Remove(cop);
        _units.Remove(cop.Unit);
    }

    private void CheckEnding(float speed, bool seen, long now, float dt)
    {
        var cornered = _nearest <= BustDistance && speed <= BustSpeed;
        _bust = Math.Clamp(_bust + (cornered ? dt / BustSeconds : -dt / BustDrainSeconds), 0, 1);
        _evade = Math.Clamp(_evade + (seen ? -dt / EvadeDrainSeconds : dt / EvadeSeconds), 0, 1);

        if (_bust >= 1)
        {
            FinishChase("busted", PhaseBusted, BustScreenMs, now);
        }
        else if (_evade >= 1)
        {
            FinishChase("escaped", PhaseEscaped, EscapedScreenMs, now);
        }
    }

    private void FinishChase(string how, byte phase, long screenMs, long now)
    {
        Log.Information("[CopBot] {Name} {How} after {Seconds:F0}s at {Stars} stars, top speed {Top:F0} km/h, {Down} cops wrecked",
            _player.Client?.Name, how, (now - _startedAt) / 1000f, _stars, _topSpeed * 3.6f, _down);
        End();
        _phase = phase;
        _phaseUntil = now + screenMs;
    }

    // A new unit comes up the road the player just drove, out of sight
    private void AddCop(int chasers, long now)
    {
        if (_route.HeadDistance - _route.StartDistance < MinRouteToJoin) return;

        var progress = MathF.Max(_route.StartDistance, _route.HeadDistance - CopUnit.JoinBehind(chasers));
        var position = _route.Sample(progress).Position;
        var speed = CopUnit.JoinSpeed(_route, progress, DriverProfile.HeatForStars(_stars));
        var driver = new CopDriver(position, CopDriver.HeadingOf(_route.Tangent(progress)), speed);

        if (Join(position, new CopUnit(driver, progress)))
        {
            _nextCop = now + JoinSpacingMs;
            Log.Information("[CopBot] {Name} now has {Count} units, new one {Gap:F0}m behind", _player.Client?.Name, _units.Count, _route.HeadDistance - progress);
        }
    }

    // A roadblock that has joined in leaves one too many. Whoever is furthest back drops out, once out of sight.
    private void SendAwayExtraCop()
    {
        var last = _cars.Where(cop => cop.Unit.IsChasing).MinBy(cop => cop.Unit.Progress);
        if (last != null && !last.Unit.Sees(_route)) Remove(last);
    }

    // Further up the road, waiting across it or coming the wrong way down it
    private void AddAmbush(long now)
    {
        _nextAmbush = now + AmbushRetryMs;
        if (_route.PlayerSpeed < AmbushMinSpeed) return;
        if (!Ambush.TryFindSpot(_route.Road, _route.PlayerPosition, _route.PlayerDirection, _route.PlayerSpeed, out var path)) return;

        var headOn = _stars >= HeadOnFromStars && _nextAmbushIsHeadOn;
        if (headOn ? AddHeadOn(path, now) : AddRoadblock(path, now))
        {
            _nextAmbushIsHeadOn = !headOn;
            _nextAmbush = now + _stars switch { <= 3 => 30_000, 4 => 24_000, _ => 18_000 };
        }
    }

    private bool AddRoadblock(List<Vector3> path, long now)
    {
        var along = Ambush.Along(path);
        var cars = 0;
        foreach (var place in Ambush.Roadblock(_route.Road, path, MaxRoadblockCars, _random.Next(2) == 0))
        {
            if (!Join(place.Position, CopUnit.ParkedAcrossRoad(new CopDriver(place.Position, place.Heading, 0), along, now))) break;
            cars++;
        }

        if (cars == 0) return false;

        Alert(AlertRoadblock);
        Log.Information("[CopBot] Roadblock of {Cars} ahead of {Name}", cars, _player.Client?.Name);
        return true;
    }

    private bool AddHeadOn(List<Vector3> path, long now)
    {
        var place = Ambush.HeadOn(path);
        var speed = CopUnit.HeadOnSpeedAt(DriverProfile.HeatForStars(_stars));
        if (!Join(place.Position, CopUnit.ComingHeadOn(new CopDriver(place.Position, place.Heading, speed), path, now))) return false;

        Alert(AlertHeadOn);
        Log.Information("[CopBot] Unit coming head on at {Name}", _player.Client?.Name);
        return true;
    }

    private bool Join(Vector3 position, CopUnit unit)
    {
        var free = _copBot.FindFreeCop(position);
        if (free == null) return false;

        var (pointId, valid) = _copBot.SplinePointNear(position);
        if (!valid) return false;

        free.Value.State.Teleport(pointId);
        _cars.Add(new CopCar(free.Value.Car, free.Value.State, unit));
        _units.Add(unit);
        return true;
    }

    private void AssignRoles()
    {
        var chasers = _units.Count(unit => unit.IsChasing);
        if (_rolesForStars == _stars && _rolesForCount == chasers) return;

        _rolesForStars = _stars;
        _rolesForCount = chasers;
        CopUnit.AssignRoles(_units, _stars, _route.HeadDistance);
    }

    private void LogChase(float speed, long now)
    {
        if (_phase != PhaseChase || now < _nextLog) return;
        _nextLog = now + LogEveryMs;

        var position = _player.Status.Position;
        var units = string.Join(", ", _cars.Select(cop =>
            $"{(cop.Unit.IsWrecked ? "wreck" : cop.Unit.Role.ToString())} {MathF.Abs(cop.Unit.Driver.Speed) * 3.6f:F0}km/h {Vector3.Distance(cop.Position, position):F0}m"));
        Log.Information("[CopBot] {Name} {Stars}* {Speed:F0}km/h bust {Bust:F0}% evade {Evade:F0}% | {Units}",
            _player.Client?.Name, _stars, speed * 3.6f, _bust * 100, _evade * 100, units);
    }

    private void SendHud(long now)
    {
        var changed = _phase != _lastHudPhase || _stars != _lastHudStars || _alertId != _lastHudAlertId;
        if (!changed && (_phase == PhaseIdle || now < _nextHud)) return;

        _nextHud = now + HudIntervalMs;
        _lastHudPhase = _phase;
        _lastHudStars = _stars;
        _lastHudAlertId = _alertId;

        var chasing = _phase == PhaseChase;
        _player.Client?.SendPacket(new CopBotState
        {
            Phase = _phase,
            Stars = (byte)_stars,
            Cops = (byte)_units.Count(unit => !unit.IsWrecked),
            Down = (byte)Math.Min(_down, 255),
            BustPercent = (byte)(chasing ? _bust * 100 : 0),
            EscapePercent = (byte)(chasing ? _evade * 100 : 0),
            Alert = _alert,
            AlertId = _alertId,
            NearestMeters = (ushort)(_cars.Count > 0 && _nearest < float.MaxValue ? Math.Clamp(_nearest, 0, 60000) : 0),
        });
    }
}

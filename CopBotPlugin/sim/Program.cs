using System.Globalization;
using System.Numerics;
using System.Text;
using CopBotPlugin;
using CopSim;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

const float Kmh = 1 / 3.6f;
const float CaprisePower = 328;  // 531 bhp, 1210 kg, from the car's ui_car.json
var outDir = args.Length > 0 ? args[0] : "out";
var only = args.Length > 1 ? args[1] : "";
Directory.CreateDirectory(outDir);
// The server's AI spline cache (the real road). Set AC_CACHE to point somewhere else.
var cacheDir = Environment.GetEnvironmentVariable("AC_CACHE")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "docker/assettocorsa/assetto/cache");
var cache = Directory.GetFiles(cacheDir, "*.aic1")[0];
var realRoad = new CacheRoad(cache);

// A lane with a neighbour, on a long run of the map
var start = 0;
for (var i = 60000; i < realRoad.Pos.Length; i += 37)
{
    if (realRoad.Left[i] >= 0 && Vector3.Dot(realRoad.Forward(realRoad.Left[i]), realRoad.Forward(i)) > 0)
    {
        start = i;
        break;
    }
}

float Waves(float s) => (110 + 50 * MathF.Sin(s / 900)) * Kmh;
var realLine = Lines.RealLane(realRoad, start, 25000, 1);

var streets = Lines.Streets(
    (300, 14, 90), (160, 12, -90), (120, 18, 90), (90, 10, 90), (200, 25, -60),
    (150, 12, 90), (80, 14, -90), (250, 9, 170), (260, 16, -90), (140, 12, 90),
    (220, 20, 45), (60, 20, -45), (300, 0, 0));

var uturn = Lines.Streets((500, 7, 180), (700, 0, 0));

var results = new StringBuilder();
results.AppendLine("scenario                     unit role      caught reach15 gapAvg gapStd gapMin | steerFlips/min maxLatG jerkP99 maxBrake | maxOffLine p95OffLine | stepErr contact | topSpeed");

// The way the user really drives: a 531 bhp car, flat out where the road allows, on the limit in the bends
ChanceTable();
Run("swerve_1star", realRoad, realLine, _ => 300 * Kmh, 1, 150, 0, 0, new SimPlayer(realLine, _ => 300 * Kmh, 11, 10, 7, CaprisePower) { SwerveEvery = 9 });
Run("swerve_5star", realRoad, realLine, _ => 300 * Kmh, 5, 150, 0, 0, new SimPlayer(realLine, _ => 300 * Kmh, 11, 10, 7, CaprisePower) { SwerveEvery = 9 });
Fast("fast_real_1star", realRoad, realLine, 1);
Fast("fast_real_2star", realRoad, realLine, 2);
Fast("fast_real_3star", realRoad, realLine, 3);
Fast("fast_real_5star", realRoad, realLine, 5);
Fast("fast_streets_1star", new NoRoad(), streets, 1);
Fast("fast_streets_3star", new NoRoad(), streets, 3);
Fast("fast_streets_5star", new NoRoad(), streets, 5);
Fast("roadblock_3star", realRoad, realLine, 3, "roadblock");
Fast("roadblock_missed_3star", realRoad, realLine, 3, "roadblock_missed");
Fast("headon_5star", realRoad, realLine, 5, "headon");
Fast("headon_missed_5star", realRoad, realLine, 5, "headon_missed");

// A slower, tidier driver, and the awkward cases
Run("real_road_1star", realRoad, realLine, Waves, 1, 150, 0, 0);
Run("real_road_3star", realRoad, realLine, Waves, 3, 150, 0, 0);
Run("real_road_5star", realRoad, realLine, Waves, 5, 150, 0, 0);
Run("streets_1star", new NoRoad(), streets, _ => 85 * Kmh, 1, 150, 0, 0);
Run("stop_and_go_1star", new NoRoad(), Lines.Streets((2500, 0, 0)), _ => 80 * Kmh, 1, 110, 50, 75);
Run("stop_and_go_5star", new NoRoad(), Lines.Streets((2500, 0, 0)), _ => 80 * Kmh, 5, 110, 50, 75);
Run("uturn_1star", new NoRoad(), uturn, _ => 70 * Kmh, 1, 80, 0, 0);
Run("uturn_5star", new NoRoad(), uturn, _ => 70 * Kmh, 5, 80, 0, 0);

Console.Write(results.ToString());
File.WriteAllText(Path.Combine(outDir, string.IsNullOrEmpty(only) ? "summary.txt" : "summary_" + only + ".txt"), results.ToString());

// How likely each outcome is after one hit, over many different drivers
void ChanceTable()
{
    if (only != "" && !"chance".Contains(only)) return;

    results.AppendLine("hit                       stars | nothing wobble lost  spin");
    var dice = new Random(5);
    var direction = Vector3.UnitZ;
    foreach (var (label, player) in new[] { ("rear corner", new Vector3(0.9f, 0, -3.5f)), ("door", new Vector3(1.8f, 0, 0)), ("dead behind", new Vector3(0, 0, -4.8f)) })
    foreach (var kmh in new[] { 15f, 30f, 50f, 80f, 120f })
    foreach (var stars in new[] { 1, 5 })
    {
        var counts = new int[4];
        for (var trial = 0; trial < 4000; trial++)
        {
            var nerve = new DriverNerve(dice);
            var (kick, yaw) = CarContact.Impulse(CarContact.Measure(Vector3.Zero, direction, player, direction), kmh / 3.6f);
            nerve.Shock(kick.Length() / 15 + MathF.Abs(yaw) / 8);
            var outcome = Mishap.None;
            for (var t = 0f; t < 5 && outcome == Mishap.None; t += 0.033f) outcome = nerve.Roll(0, 0, DriverProfile.HeatForStars(stars), 0.033f);
            counts[(int)outcome]++;
        }

        results.AppendLine($"{label,-13} {kmh,4:F0} km/h {stars,5} | {counts[0] / 40f,6:F0}% {counts[1] / 40f,5:F0}% {counts[2] / 40f,4:F0}% {counts[3] / 40f,4:F0}%");
    }
}

void Fast(string name, IRoad road, List<Vector3> line, int stars, string? ambush = null) =>
    Run(name, road, line, _ => 300 * Kmh, stars, 150, 0, 0, new SimPlayer(line, _ => 300 * Kmh, 11, 10, 7, CaprisePower), ambush);

void Run(string name, IRoad road, List<Vector3> line, Func<float, float> top, int stars, float seconds, float stopAt, float goAt, SimPlayer? fast = null, string? ambush = null)
{
    if (only != "" && !name.Contains(only)) return;

    var player = fast ?? new SimPlayer(line, top, 6, 6, 7);
    if (stopAt > 0)
    {
        player.StopAt = stopAt;
        player.GoAt = goAt;
    }

    CopUnit.Dice = new Random(11);
    var mishaps = new Dictionary<string, int>();
    var route = new PlayerRoute(road);
    var random = new Random(3);
    var heat = DriverProfile.HeatForStars(stars);
    var lastHit = new Dictionary<CopUnit, long>();
    var units = new List<CopUnit>();
    var metrics = new Dictionary<CopUnit, Metrics>();
    var body = new StringBuilder();

    var time = 0f;
    var now = 0L;
    var assigned = -1;
    var seen = Vector3.Zero;
    var seenVelocity = Vector3.Zero;
    var ordered = new List<CopUnit>();
    var trueLine = new List<Vector3>();
    var hits = 0;
    var wrecks = 0;
    var nextCop = 22000L;
    var topPlayer = 0f;
    var ambushDone = false;
    var ambushNote = "";
    var seenFor = 0f;
    var dodgers = new HashSet<CopUnit>();
    while (time < seconds && !player.Finished)
    {
        var dt = (33 + random.Next(-4, 5)) / 1000f;
        time += dt;
        now += (long)(dt * 1000);
        player.Step(dt);
        topPlayer = MathF.Max(topPlayer, player.Speed);

        // Network: some ticks bring no new position
        if (random.NextDouble() > 0.12)
        {
            seen = player.Position;
            seenVelocity = player.Velocity;
        }

        route.Update(seen, seenVelocity, 0, dt);
        trueLine.Add(player.Position);

        var chasers = units.Count(unit => unit.IsChasing);
        if (now >= nextCop && chasers < CopUnit.UnitsForStars(stars) && route.HeadDistance - route.StartDistance > 100)
        {
            var d = MathF.Max(route.StartDistance, route.HeadDistance - CopUnit.JoinBehind(chasers));
            var tangent = route.Tangent(d);
            var driver = new CopDriver(route.Sample(d).Position, CopDriver.HeadingOf(tangent), CopUnit.JoinSpeed(route, d, heat));
            Add(new CopUnit(driver, d));
            nextCop = now + 1500;
        }

        void Add(CopUnit unit)
        {
            units.Add(unit);
            ordered.Add(unit);
            metrics[unit] = new Metrics(unit.Driver);
        }

        if (ambush != null && !ambushDone && time > 60 && route.PlayerSpeed > 20
            && Ambush.TryFindSpot(road, route.PlayerPosition, route.PlayerDirection, route.PlayerSpeed, out var path))
        {
            ambushDone = true;
            var ahead = (path[^1] - route.PlayerPosition).Length();
            if (ambush.StartsWith("roadblock"))
            {
                var places = Ambush.Roadblock(road, path, 3, false);
                foreach (var place in places) Add(CopUnit.ParkedAcrossRoad(new CopDriver(place.Position, place.Heading, 0), Ambush.Along(path), now));
                ambushNote = $"roadblock {places.Count} cars {ahead:F0}m ahead at t={time:F0}, player {route.PlayerSpeed * 3.6f:F0}km/h";
            }
            else
            {
                var place = Ambush.HeadOn(path);
                Add(CopUnit.ComingHeadOn(new CopDriver(place.Position, place.Heading, CopUnit.HeadOnSpeedAt(heat)), path, now));
                ambushNote = $"headon {ahead:F0}m ahead at t={time:F0}, player {route.PlayerSpeed * 3.6f:F0}km/h";
            }
        }

        var chasing = units.Count(unit => unit.IsChasing);
        if (chasing != assigned)
        {
            assigned = chasing;
            CopUnit.AssignRoles(units, stars, route.HeadDistance);
        }

        // A careful driver slows for a police car stopping them in their lane. The fast one goes round it,
        // which tests whether the cops can keep up with someone who won't be stopped.
        player.Cap = float.MaxValue;
        if (fast == null)
        {
            foreach (var unit in units)
            {
                if (!unit.IsChasing) continue;
                var to = (unit.Driver.Position - player.Position) with { Y = 0 };
                var ahead = Vector3.Dot(to, route.PlayerDirection);
                if (ahead > 2 && ahead < 30 && (to - route.PlayerDirection * ahead).Length() < 2) player.Cap = MathF.Min(player.Cap, unit.Driver.Speed);
            }
        }

        foreach (var unit in units)
        {
            unit.Update(route, units, heat, now, dt);
            if (unit.Incident == null) continue;

            var kind = unit.Incident.Split(' ')[0];
            mishaps[kind] = mishaps.GetValueOrDefault(kind) + 1;
            unit.Incident = null;
        }

        if (units.Any(unit => unit.Sees(route))) seenFor += dt;

        // The real game reports contact between cars. Here it is decided by distance.
        foreach (var unit in units)
        {
            var apart = Vector3.Distance(unit.Driver.Position with { Y = 0 }, player.Position with { Y = 0 });
            lastHit.TryGetValue(unit, out var last);

            // Dodged: the head-on and roadblock cars miss, to see what they do next
            if (unit.IsAmbush) dodgers.Add(unit);
            if (ambush?.EndsWith("_missed") == true && dodgers.Contains(unit)) continue;

            if (apart < 3.6f && now - last > 1000 && !unit.IsWrecked)
            {
                lastHit[unit] = now;
                var impact = (unit.Driver.Velocity - player.Velocity).Length() * 3.6f;
                unit.OnHit(player.Position, route.PlayerDirection, impact, now);
                hits++;
                if (unit.IsWrecked)
                {
                    wrecks++;
                    nextCop = now + 14000 - stars * 2000;
                }
            }
        }

        foreach (var unit in units)
        {
            if (unit.IsAmbush || unit.IsWrecked) continue;
            var distance = Vector3.Distance(unit.Driver.Position with { Y = 0 }, player.Position with { Y = 0 });
            metrics[unit].Record(unit, route, trueLine, distance, dt);
        }

        body.Append($"{time:F3},{player.Position.X:F2},{player.Position.Z:F2},{player.Speed:F2}");
        foreach (var unit in ordered)
        {
            var d = unit.Driver;
            var g = unit.IsAmbush ? 0 : route.HeadDistance - unit.Progress;
            body.Append($",{d.Position.X:F2},{d.Position.Z:F2},{d.Speed:F2},{d.Acceleration:F2},{d.WheelAngle:F4},{g:F2},{unit.Lateral:F2}");
        }

        body.AppendLine();
    }

    var header = new StringBuilder("t,px,pz,pv");
    for (var i = 0; i < ordered.Count; i++) header.Append($",x{i},z{i},v{i},a{i},w{i},g{i},l{i}");
    File.WriteAllText(Path.Combine(outDir, name + ".csv"), header + "\n" + body);

    results.AppendLine($"{name,-28} player top {topPlayer * 3.6f:F0}km/h avg {route.HeadDistance / time * 3.6f:F0}km/h over {time:F0}s, hits {hits}, wrecks {wrecks}, seen {seenFor / time * 100:F0}%, mistakes {string.Join(" ", mishaps.Select(m => m.Key + " " + m.Value))} {ambushNote}");
    for (var i = 0; i < ordered.Count; i++)
    {
        results.AppendLine($"{name,-28} {i,4} {ordered[i].Role,-9} {metrics[ordered[i]].Report()}");
    }
}

public sealed class Metrics
{
    private readonly CopDriver _driver;
    private readonly List<float> _gaps = new();
    private readonly List<float> _jerks = new();
    private readonly List<float> _off = new();
    private float _caughtAt = -1;
    private float _reachedAt = -1;
    private float _time;
    private int _flips;
    private float _lastWheelSign;
    private float _lastAccel;
    private float _maxLat;
    private float _maxBrake;
    private float _maxSpeed;
    private float _stepError;
    private float _minContact = float.MaxValue;
    private Vector3 _lastPosition;
    private Vector3 _lastVelocity;
    private bool _first = true;
    private int _searchFrom;

    public Metrics(CopDriver driver) => _driver = driver;

    public void Record(CopUnit unit, PlayerRoute route, List<Vector3> trueLine, float distance, float dt)
    {
        _time += dt;
        var gap = route.HeadDistance - unit.Progress;
        if (_caughtAt < 0 && gap < 50) _caughtAt = _time;
        if (_reachedAt < 0 && gap < 15) _reachedAt = _time;

        if (!_first)
        {
            // How far the car moved compared with where its reported velocity said it would be
            var expected = _lastPosition + _lastVelocity * dt;
            _stepError = MathF.Max(_stepError, Vector3.Distance(expected with { Y = 0 }, _driver.Position with { Y = 0 }));

            var jerk = MathF.Abs(_driver.Acceleration - _lastAccel) / dt;
            _jerks.Add(jerk);

            if (MathF.Abs(_driver.WheelAngle) > 0.012f)
            {
                var sign = MathF.Sign(_driver.WheelAngle);
                if (sign != _lastWheelSign && _lastWheelSign != 0) _flips++;
                _lastWheelSign = sign;
            }
        }

        _first = false;
        _lastPosition = _driver.Position;
        _lastVelocity = _driver.Velocity;
        _lastAccel = _driver.Acceleration;

        var lat = _driver.Speed * _driver.Speed * MathF.Tan(MathF.Abs(_driver.WheelAngle)) / CopDriver.Wheelbase;
        _maxLat = MathF.Max(_maxLat, lat);
        _maxBrake = MathF.Max(_maxBrake, -_driver.Acceleration);
        _maxSpeed = MathF.Max(_maxSpeed, _driver.Speed);
        _minContact = MathF.Min(_minContact, distance);

        if (_caughtAt >= 0)
        {
            _gaps.Add(gap);

            // Distance from the line the player really drove
            var best = float.MaxValue;
            var from = _searchFrom == 0 ? 0 : Math.Max(0, _searchFrom - 40);
            var to = _searchFrom == 0 ? trueLine.Count : Math.Min(trueLine.Count, _searchFrom + 400);
            for (var i = from; i < to; i++)
            {
                var d = Vector3.DistanceSquared(trueLine[i] with { Y = 0 }, _driver.Position with { Y = 0 });
                if (d < best)
                {
                    best = d;
                    _searchFrom = i;
                }
            }
            _off.Add(MathF.Sqrt(best));
        }
    }

    public string Report()
    {
        var caught = _caughtAt < 0 ? "never" : $"{_caughtAt,4:F0}s";
        var reached = _reachedAt < 0 ? "never" : $"{_reachedAt,4:F0}s";
        if (_gaps.Count == 0) return $"{caught} maxLatG {_maxLat / 9.81f:F2}";

        var mean = _gaps.Average();
        var std = MathF.Sqrt(_gaps.Select(g => (g - mean) * (g - mean)).Average());
        _jerks.Sort();
        _off.Sort();
        var minutes = _time / 60;
        return $"{caught,6} {reached,7} {mean,6:F1} {std,6:F1} {_gaps.Min(),6:F1} | {_flips / minutes,14:F1} {_maxLat / 9.81f,7:F2} {_jerks[(int)(_jerks.Count * 0.99f)],7:F1} {_maxBrake,8:F1} | {_off[^1],10:F1} {_off[(int)(_off.Count * 0.95f)],10:F1} | {_stepError,7:F3} {_minContact,7:F1} | {_maxSpeed * 3.6f,6:F0}";
    }
}

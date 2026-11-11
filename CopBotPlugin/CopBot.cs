using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Server.Ai;
using AssettoServer.Server.Ai.Splines;
using Serilog;

namespace CopBotPlugin;

/// <summary>
/// Police bots for online play. Watches every player, runs a pursuit for each, and keeps the
/// cops that aren't chasing anyone out of sight so the roads have no normal traffic.
/// </summary>
public sealed class CopBot
{
    private const string CopModel = "pb_ford_crownvic_cop";
    private static readonly Vector3 HiddenPosition = new(0, -5000, 0);

    private static readonly FieldInfo AiStatesField =
        typeof(EntryCar).GetField("_aiStates", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("EntryCar._aiStates not found");

    private static readonly MethodInfo SendCurrentSession =
        typeof(SessionManager).GetMethod("SendCurrentSession", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("SessionManager.SendCurrentSession not found");

    private readonly EntryCarManager _entryCarManager;
    private readonly SessionManager _sessionManager;
    private readonly AiSpline _spline;
    private readonly IRoad _road;
    private readonly SteeringCalibration _steering = new();
    private readonly Dictionary<EntryCar, Pursuit> _pursuits = new();
    private readonly ConcurrentQueue<(EntryCar Player, EntryCar Target, float Impact)> _collisions = new();
    private long _lastTick;

    public CopBot(ACServer server, EntryCarManager entryCarManager, AiSpline spline, SessionManager sessionManager, CSPServerScriptProvider scriptProvider)
    {
        _entryCarManager = entryCarManager;
        _sessionManager = sessionManager;
        _spline = spline;
        _road = new SplineRoad(spline);

        using var hud = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("CopBotPlugin.lua.copbot_hud.lua")!);
        scriptProvider.AddScript(hud.ReadToEnd(), "copbot_hud.lua");

        _entryCarManager.ClientConnected += (client, _) => client.Collision += OnCollision;

        // Subscribing late puts this after the normal traffic update, so what is written here is what gets sent
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            server.Update += OnUpdate;
            Log.Information("[CopBot] Active");
        });
    }

    /// <summary>The nearest cop nobody is using, to join a chase.</summary>
    public (EntryCar Car, AiState State)? FindFreeCop(Vector3 near)
    {
        var best = _entryCarManager.EntryCars
            .Where(car => car.AiControlled && car.Model == CopModel && !IsInChase(car))
            .Select(car => (Car: car, Closest: car.GetClosestAiState(near)))
            .Where(c => c.Closest.AiState != null)
            .OrderBy(c => c.Closest.DistanceSquared)
            .FirstOrDefault();

        return best.Car == null ? null : (best.Car, best.Closest.AiState!);
    }

    public static List<AiState> StatesOf(EntryCar car) => (List<AiState>)AiStatesField.GetValue(car)!;

    public (int PointId, bool Valid) SplinePointNear(Vector3 position)
    {
        var (pointId, _) = _spline.WorldToSpline(position);
        return (pointId, pointId >= 0 && _spline.Points[pointId].NextId >= 0);
    }

    // Resending the session puts the player back in the pits
    public void SendToPits(EntryCar player) => SendCurrentSession.Invoke(_sessionManager, [player.Client]);

    // Arrives on a network thread, handled in the next tick
    private void OnCollision(ACTcpClient sender, CollisionEventArgs args)
    {
        if (args.TargetCar != null) _collisions.Enqueue((sender.EntryCar, args.TargetCar, args.Speed));
    }

    private void OnUpdate(ACServer sender, EventArgs args)
    {
        try
        {
            var now = _sessionManager.ServerTimeMilliseconds;
            var dt = Math.Clamp((now - _lastTick) / 1000f, 0.001f, 0.1f);
            _lastTick = now;

            while (_collisions.TryDequeue(out var hit))
            {
                if (_pursuits.TryGetValue(hit.Player, out var pursuit)) pursuit.OnCollision(hit.Target, hit.Impact, now);
            }

            foreach (var car in _entryCarManager.EntryCars)
            {
                if (car.AiControlled || car.Client?.HasSentFirstUpdate != true || car.Model == CopModel)
                {
                    if (_pursuits.Remove(car, out var gone)) gone.End();
                    continue;
                }

                if (!_pursuits.TryGetValue(car, out var pursuit))
                {
                    pursuit = new Pursuit(car, this, _road, _steering);
                    _pursuits[car] = pursuit;
                }

                pursuit.Update(now, dt);
            }

            HideIdleCops(now);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CopBot] Error in update");
        }
    }

    private bool IsInChase(EntryCar car) => _pursuits.Values.Any(pursuit => pursuit.Uses(car));

    private void HideIdleCops(long now)
    {
        foreach (var car in _entryCarManager.EntryCars)
        {
            if (!car.AiControlled || IsInChase(car)) continue;

            foreach (var state in StatesOf(car))
            {
                state.Status.Timestamp = now;
                state.Status.Position = HiddenPosition;
                state.Status.Velocity = Vector3.Zero;
            }
        }
    }
}

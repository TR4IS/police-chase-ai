namespace CopBotPlugin;

public enum Mishap
{
    None,

    /// <summary>A twitch and a lift off the throttle.</summary>
    Wobble,

    /// <summary>Runs wide and loses track of the player for a few seconds.</summary>
    LostTrack,

    /// <summary>Gone: the car spins and the driver has to gather it up.</summary>
    Spin
}

/// <summary>
/// Each cop is a different person. Hits, sudden moves by the player and driving near the limit build
/// up stress, and the more stress against that driver's composure, the higher the chance per second
/// of a mistake. Nothing is scripted: the same hit may do nothing to one driver and spin another.
/// </summary>
public sealed class DriverNerve
{
    private const float StressSeconds = 2.5f;
    private const float HazardPerSecond = 1.2f;
    private const float ComposureMean = 0.55f;
    private const float ComposureSpread = 0.15f;
    private const float ComposureHot = 0.2f;
    private const float WobbleBelow = 0.25f;
    private const float LostTrackBelow = 0.6f;
    private const float SpinTakesStress = 0.25f;
    private const float StressKeptAfterMishap = 0.3f;
    private const float AttackPerSecond = 0.12f;
    private const float AttackPerSecondHot = 0.3f;
    private const float AggressionSpread = 0.3f;

    private readonly Random _dice;
    private readonly float _composure;
    private readonly float _aggression;

    public DriverNerve(Random dice)
    {
        _dice = dice;
        _composure = Math.Clamp(Gaussian(ComposureMean, ComposureSpread), 0.25f, 0.9f);
        _aggression = Math.Clamp(Gaussian(1, AggressionSpread), 0.4f, 1.6f);
    }

    public float Stress { get; private set; }

    /// <summary>How close to making a mistake when the dice last rolled, where 1 is out of their depth.</summary>
    public float Risk { get; private set; }

    public void Shock(float amount) => Stress += amount;

    /// <summary>One moment behind the wheel. Surprise and load are this moment's, shocks stay with them for a while.</summary>
    public Mishap Roll(float surprise, float load, float heat, float dt)
    {
        Stress *= MathF.Max(0, 1 - dt / StressSeconds);

        Risk = (Stress + surprise + load) / (_composure + ComposureHot * heat);
        var chance = 1 - MathF.Exp(-HazardPerSecond * Risk * Risk * Risk * dt);
        if (_dice.NextSingle() >= chance) return Mishap.None;

        // A misjudgement can't spin a car by itself, that takes something having really upset it
        var severity = _dice.NextSingle() * Risk;
        var upset = Stress >= SpinTakesStress;
        Stress *= StressKeptAfterMishap;
        return severity < WobbleBelow ? Mishap.Wobble : severity < LostTrackBelow || !upset ? Mishap.LostTrack : Mishap.Spin;
    }

    /// <summary>Whether this driver goes for it right now. Some are keener than others, and the hotter the chase the keener.</summary>
    public bool DecidesToAttack(float heat, float dt) =>
        _dice.NextSingle() < (AttackPerSecond + AttackPerSecondHot * heat) * _aggression * dt;

    /// <summary>A length of time that is usually short and sometimes long.</summary>
    public long SampleMs(long least, long averageExtra) =>
        least + (long)(-MathF.Log(1 - _dice.NextSingle() * 0.98f) * averageExtra);

    public float Between(float low, float high) => low + (high - low) * _dice.NextSingle();

    public float EitherSide() => _dice.Next(2) == 0 ? -1 : 1;

    private float Gaussian(float mean, float spread)
    {
        var u = 1 - _dice.NextSingle();
        return mean + spread * MathF.Sqrt(-2 * MathF.Log(u)) * MathF.Cos(2 * MathF.PI * _dice.NextSingle());
    }
}

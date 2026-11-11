namespace CopBotPlugin;

/// <summary>
/// How a cop drives. Like the police in an arcade racer they are in a car with more in it than it
/// looks: quick from the first star, and the hotter the chase the closer to the limit and the less polite.
/// </summary>
public readonly record struct DriverProfile(
    float Heat,
    float BasePace,
    float CatchUpNear,
    float CatchUpFar,
    float Acceleration,
    float PowerPerKg,
    float Braking,
    float HardBraking,
    float CornerGrip,
    float GripLimit,
    float Headway,
    float MinGap,
    float LookaheadTime,
    float SteerRate,
    float LaneChangeRate,
    float ReactionTime)
{
    private const float Kmh = 1 / 3.6f;
    private const float CatchUpFullAt = 150;
    private const float MaxSpeed = 420 * Kmh;

    private static readonly DriverProfile Calm = new(
        Heat: 0,
        BasePace: 210 * Kmh,
        CatchUpNear: 20 * Kmh,
        CatchUpFar: 70 * Kmh,
        Acceleration: 8,
        PowerPerKg: 380,
        Braking: 9,
        HardBraking: 12,
        CornerGrip: 10,
        GripLimit: 15,
        Headway: 0.3f,
        MinGap: 3.5f,
        LookaheadTime: 0.55f,
        SteerRate: 0.9f,
        LaneChangeRate: 2.5f,
        ReactionTime: 0.25f);

    private static readonly DriverProfile Hot = new(
        Heat: 1,
        BasePace: 300 * Kmh,
        CatchUpNear: 35 * Kmh,
        CatchUpFar: 120 * Kmh,
        Acceleration: 11,
        PowerPerKg: 650,
        Braking: 12,
        HardBraking: 14,
        CornerGrip: 14,
        GripLimit: 18,
        Headway: 0.12f,
        MinGap: 1.5f,
        LookaheadTime: 0.45f,
        SteerRate: 1.4f,
        LaneChangeRate: 4,
        ReactionTime: 0.12f);

    public static float HeatForStars(int stars) => stars switch { <= 1 => 0f, 2 => 0.25f, 3 => 0.55f, 4 => 0.8f, _ => 1f };

    public static DriverProfile ForHeat(float heat)
    {
        heat = Math.Clamp(heat, 0, 1);
        return new DriverProfile(
            heat,
            Mix(Calm.BasePace, Hot.BasePace, heat),
            Mix(Calm.CatchUpNear, Hot.CatchUpNear, heat),
            Mix(Calm.CatchUpFar, Hot.CatchUpFar, heat),
            Mix(Calm.Acceleration, Hot.Acceleration, heat),
            Mix(Calm.PowerPerKg, Hot.PowerPerKg, heat),
            Mix(Calm.Braking, Hot.Braking, heat),
            Mix(Calm.HardBraking, Hot.HardBraking, heat),
            Mix(Calm.CornerGrip, Hot.CornerGrip, heat),
            Mix(Calm.GripLimit, Hot.GripLimit, heat),
            Mix(Calm.Headway, Hot.Headway, heat),
            Mix(Calm.MinGap, Hot.MinGap, heat),
            Mix(Calm.LookaheadTime, Hot.LookaheadTime, heat),
            Mix(Calm.SteerRate, Hot.SteerRate, heat),
            Mix(Calm.LaneChangeRate, Hot.LaneChangeRate, heat),
            Mix(Calm.ReactionTime, Hot.ReactionTime, heat));
    }

    /// <summary>
    /// A cop far enough back not to be watched. Nobody sees it take a corner too fast, and it gets back in the chase.
    /// </summary>
    public DriverProfile CatchingUp(float amount) => this with
    {
        Acceleration = Acceleration * (1 + 0.25f * amount),
        Braking = Braking * (1 + 0.25f * amount),
        HardBraking = HardBraking * (1 + 0.25f * amount),
        CornerGrip = CornerGrip * (1 + amount),
        GripLimit = GripLimit * (1 + 3 * amount)
    };

    /// <summary>
    /// The rubber band: however fast the player goes a cop can go faster, by more the further back it is.
    /// </summary>
    public float Pace(float playerSpeed, float behind)
    {
        var far = Math.Clamp(behind / CatchUpFullAt, 0, 1);
        return MathF.Min(MaxSpeed, MathF.Max(BasePace, playerSpeed + CatchUpNear + (CatchUpFar - CatchUpNear) * far));
    }

    private static float Mix(float calm, float hot, float heat) => calm + (hot - calm) * heat;
}

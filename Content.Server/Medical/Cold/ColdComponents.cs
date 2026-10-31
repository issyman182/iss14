using Robust.Shared.GameObjects;

namespace Content.Server.Medical.Cold;

/// <summary>
/// Tracks a temporary cold infection.
/// </summary>
[RegisterComponent, Access(typeof(ColdSystem))]
public sealed partial class ColdInfectionComponent : Component
{
    /// <summary>
    /// The next time the infection deals its periodic damage.
    /// </summary>
    public TimeSpan NextDamageTime;

    /// <summary>
    /// The next time this infection attempts to spread.
    /// </summary>
    public TimeSpan NextSpreadTime;

    /// <summary>
    /// When the infection ends.
    /// </summary>
    public TimeSpan EndTime;
}

/// <summary>
/// Configuration for the Cold station event.
/// </summary>
[RegisterComponent, Access(typeof(ColdRuleSystem))]
public sealed partial class ColdRuleComponent : Component
{
}

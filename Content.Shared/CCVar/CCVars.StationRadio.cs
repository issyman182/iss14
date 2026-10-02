using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// iss14: client-side volume multiplier for station radio music (from Starlight PR #6081).
    /// </summary>
    public static readonly CVarDef<float> StationRadioVolume =
        CVarDef.Create("audio.station_radio_volume", 1f, CVar.CLIENTONLY | CVar.ARCHIVE);
}

// iss14: Economy
using Robust.Shared.GameStates;

namespace Content.Shared.Economy.Store;

/// <summary>
/// A bought title worn by a character (and stamped on their ID card). Shown in examine and in front of the
/// name in local chat.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CharacterTitleComponent : Component
{
    [DataField, AutoNetworkedField]
    public string Title = string.Empty;
}

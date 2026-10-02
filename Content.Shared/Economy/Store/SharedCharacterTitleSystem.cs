// iss14: Economy
using Content.Shared.Chat;
using Content.Shared.Examine;

namespace Content.Shared.Economy.Store;

/// <summary>Shows a <see cref="CharacterTitleComponent"/> in examine and prefixes it to the chat name.</summary>
public sealed partial class SharedCharacterTitleSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CharacterTitleComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<CharacterTitleComponent, TransformSpeakerNameEvent>(OnSpeakerName);
    }

    private void OnExamined(Entity<CharacterTitleComponent> ent, ref ExaminedEvent args)
    {
        if (string.IsNullOrWhiteSpace(ent.Comp.Title))
            return;

        args.PushMarkup(Loc.GetString("character-title-examine", ("title", ent.Comp.Title)), priority: 5);
    }

    private void OnSpeakerName(Entity<CharacterTitleComponent> ent, ref TransformSpeakerNameEvent args)
    {
        if (string.IsNullOrWhiteSpace(ent.Comp.Title))
            return;

        // Only decorate the real name; a voice mask already replaced it with something else.
        if (args.VoiceName == Name(ent))
            args.VoiceName = Loc.GetString("character-title-chat-name", ("title", ent.Comp.Title), ("name", args.VoiceName));
    }
}

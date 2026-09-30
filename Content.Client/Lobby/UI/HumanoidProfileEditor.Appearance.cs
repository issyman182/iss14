using System.Linq;
using Content.Client.UserInterface.Systems.Guidebook;
using Content.Shared.CCVar;
using Content.Shared.Chat.Prototypes;
using Content.Shared.Guidebook;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Preferences;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    public event Action<List<ProtoId<GuideEntryPrototype>>>? OnOpenGuidebook;

    private ColorSelectorSliders _rgbSkinColorSelector;
    private List<SpeciesPrototype> _species = new();
    private List<EmoteSoundsPrototype> _voices = new(); // iss14: upstream voice picker
    private static readonly ProtoId<GuideEntryPrototype> DefaultSpeciesGuidebook = "Species";

    public void UpdateSpeciesGuidebookIcon()
    {
        SpeciesInfoButton.StyleClasses.Clear();

        var species = Profile?.Species;
        if (species is null)
            return;

        if (!_prototypeManager.Resolve<SpeciesPrototype>(species, out var speciesProto))
            return;

        // Don't display the info button if no guide entry is found
        if (!_prototypeManager.HasIndex<GuideEntryPrototype>(species))
            return;

        const string style = "SpeciesInfoDefault";
        SpeciesInfoButton.StyleIdentifier = style;
    }

    private void UpdateGenderControls()
    {
        if (Profile == null)
        {
            return;
        }

        PronounsButton.SelectId((int)Profile.Gender);
    }

    private void UpdateAgeEdit()
    {
        AgeEdit.Text = Profile?.Age.ToString() ?? "";
    }

    private void UpdateSexControls()
    {
        if (Profile == null)
            return;

        SexButton.Clear();

        var sexes = new List<Sex>();

        // add species sex options, default to just none if we are in bizzaro world and have no species
        if (_prototypeManager.Resolve(Profile.Species, out var speciesProto))
        {
            foreach (var sex in speciesProto.Sexes)
            {
                sexes.Add(sex);
            }
        }
        else
        {
            sexes.Add(Sex.Unsexed);
        }

        // add button for each sex
        foreach (var sex in sexes)
        {
            SexButton.AddItem(Loc.GetString($"humanoid-profile-editor-sex-{sex.ToString().ToLower()}-text"), (int)sex);
        }

        if (sexes.Contains(Profile.Sex))
            SexButton.SelectId((int)Profile.Sex);
        else
            SexButton.SelectId((int)sexes[0]);
    }

    private void UpdateEyePickers()
    {
        if (Profile == null)
        {
            return;
        }

        Markings.CurrentEyeColor = Profile.Appearance.EyeColor;
        EyeColorPicker.SetData(Profile.Appearance.EyeColor);
    }

    private void UpdateSkinColor()
    {
        if (Profile == null)
            return;

        var skin = _prototypeManager.Index<SpeciesPrototype>(Profile.Species).SkinColoration;

        switch (skin)
        {
            case HumanoidSkinColor.HumanToned:
                {
                    if (!Skin.Visible)
                    {
                        Skin.Visible = true;
                        RgbSkinColorContainer.Visible = false;
                    }

                    Skin.Value = SkinColor.HumanSkinToneFromColor(Profile.Appearance.SkinColor);

                    break;
                }
            case HumanoidSkinColor.Hues:
                {
                    if (!RgbSkinColorContainer.Visible)
                    {
                        Skin.Visible = false;
                        RgbSkinColorContainer.Visible = true;
                    }

                    // set the RGB values to the direct values otherwise
                    _rgbSkinColorSelector.Color = Profile.Appearance.SkinColor;
                    break;
                }
            case HumanoidSkinColor.TintedHues:
                {
                    if (!RgbSkinColorContainer.Visible)
                    {
                        Skin.Visible = false;
                        RgbSkinColorContainer.Visible = true;
                    }

                    // set the RGB values to the direct values otherwise
                    _rgbSkinColorSelector.Color = Profile.Appearance.SkinColor;
                    break;
                }
            case HumanoidSkinColor.VoxFeathers:
                {
                    if (!RgbSkinColorContainer.Visible)
                    {
                        Skin.Visible = false;
                        RgbSkinColorContainer.Visible = true;
                    }

                    _rgbSkinColorSelector.Color = SkinColor.ClosestVoxColor(Profile.Appearance.SkinColor);

                    break;
                }
            case HumanoidSkinColor.NoColor: // Goob #1161
                {
                    if (!RgbSkinColorContainer.Visible)
                    {
                        Skin.Visible = false;
                        RgbSkinColorContainer.Visible = true;
                    }

                    _rgbSkinColorSelector.Color = Color.FromName("White");

                    break;
                }
            // Goobstation Section Start - Tajaran
            case HumanoidSkinColor.AnimalFur: // Goobstation - Tajaran
                {
                    if (!RgbSkinColorContainer.Visible)
                    {
                        Skin.Visible = false;
                        RgbSkinColorContainer.Visible = true;
                    }

                    _rgbSkinColorSelector.Color = SkinColor.ClosestAnimalFurColor(Profile.Appearance.SkinColor);
                    break;
                }
            // Goobstation Section End - Tajaran
        }
    }

    private void UpdateSpawnPriorityControls()
    {
        if (Profile == null)
        {
            return;
        }

        SpawnPriorityButton.SelectId((int)Profile.SpawnPriority);
    }

    /// <summary>
    /// Refreshes the species selector.
    /// </summary>
    public void RefreshSpecies()
    {
        SpeciesButton.Clear();
        _species.Clear();

        _species.AddRange(_prototypeManager.EnumeratePrototypes<SpeciesPrototype>().Where(o => o.RoundStart));
        _species.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        var speciesIds = _species.Select(o => o.ID).ToList();

        for (var i = 0; i < _species.Count; i++)
        {
            var name = Loc.GetString(_species[i].Name);
            SpeciesButton.AddItem(name, i);

            if (Profile?.Species.Equals(_species[i].ID) == true)
            {
                SpeciesButton.SelectId(i);
            }
        }

        // If our species isn't available then reset it to default.
        if (Profile != null)
        {
            if (!speciesIds.Contains(Profile.Species))
            {
                SetSpecies(HumanoidCharacterProfile.DefaultSpecies);
            }
        }
    }

    private void SetSpecies(string newSpecies)
    {
        Profile = Profile?.WithSpecies(newSpecies);
        OnSkinColorOnValueChanged(); // Species may have special color prefs, make sure to update it.
        Markings.SetSpecies(newSpecies); // Repopulate the markings tab as well.
        // In case there's job restrictions for the species
        RefreshJobs();
        // In case there's species restrictions for loadouts
        RefreshLoadouts();
        UpdateSexControls(); // update sex for new species
        UpdateVoiceControls(); // iss14: voices are per-species
        UpdateSpeciesGuidebookIcon();
        // In case there's species restrictions for bark voices
        if (_cfgManager.GetCVar(CCVars.TtsEnabled))
            UpdateBarkVoice();
        ReloadPreview();
    }

    private void SetAge(int newAge)
    {
        Profile = Profile?.WithAge(newAge);
        ReloadPreview();
    }

    // iss14: upstream voice picker
    private void UpdateVoiceControls()
    {
        if (Profile == null)
            return;

        VoiceButton.Clear();
        _voices.Clear();

        if (!_prototypeManager.TryIndex(Profile.Species, out var speciesPrototype))
            return;

        var availableVoices = speciesPrototype.Voices;

        _voices.AddRange(availableVoices.Select(protoId => _prototypeManager.Index(protoId)));

        if (_voices.All(proto => Profile?.Voice != proto.ID))
            SetVoice(speciesPrototype.DefaultSoundsBySex[(int)Profile.Sex]);

        for (var i = 0; i < _voices.Count; i++)
        {
            var name = Loc.GetString(_voices[i].VoiceSelectorName);
            VoiceButton.AddItem(name, i);

            if (Profile?.Voice.Equals(_voices[i].ID) == true)
            {
                VoiceButton.SelectId(i);
            }
        }
    }

    // iss14: upstream voice picker
    private void SetVoice(ProtoId<EmoteSoundsPrototype> newVoice)
    {
        Profile = Profile?.WithVoice(newVoice);
        SetDirty();
    }

    private void SetSex(Sex newSex)
    {
        Profile = Profile?.WithSex(newSex);
        // for convenience, default to most common gender when new sex is selected
        switch (newSex)
        {
            case Sex.Male:
                Profile = Profile?.WithGender(Gender.Male);
                break;
            case Sex.Female:
                Profile = Profile?.WithGender(Gender.Female);
                break;
            default:
                Profile = Profile?.WithGender(Gender.Epicene);
                break;
        }

        // iss14: keep the voice matching the new sex's default like upstream does
        // iss14 fix: Profile may be null here (upstream uses ?.) - TryIndex(ProtoId?) handles null.
        if (_prototypeManager.TryIndex(Profile?.Species, out var sexSpeciesProto))
            SetVoice(sexSpeciesProto.DefaultSoundsBySex[(int)newSex]);

        UpdateGenderControls();
        UpdateVoiceControls();
        Markings.SetSex(newSex);
        ReloadPreview();
    }

    private void SetGender(Gender newGender)
    {
        Profile = Profile?.WithGender(newGender);
        ReloadPreview();
    }

    private void SetSpawnPriority(SpawnPriorityPreference newSpawnPriority)
    {
        Profile = Profile?.WithSpawnPriorityPreference(newSpawnPriority);
        SetDirty();
    }

    private void OnSpeciesInfoButtonPressed(BaseButton.ButtonEventArgs args)
    {
        // TODO GUIDEBOOK
        // make the species guide book a field on the species prototype.
        // I.e., do what jobs/antags do.

        var guidebookController = UserInterfaceManager.GetUIController<GuidebookUIController>();
        var species = Profile?.Species ?? HumanoidCharacterProfile.DefaultSpecies;
        var page = DefaultSpeciesGuidebook;
        if (_prototypeManager.HasIndex<GuideEntryPrototype>(species))
            page = new ProtoId<GuideEntryPrototype>(species.Id); // Gross. See above todo comment.

        if (_prototypeManager.Resolve(DefaultSpeciesGuidebook, out var guideRoot))
        {
            var dict = new Dictionary<ProtoId<GuideEntryPrototype>, GuideEntry>();
            dict.Add(DefaultSpeciesGuidebook, guideRoot);
            //TODO: Don't close the guidebook if its already open, just go to the correct page
            guidebookController.OpenGuidebook(dict, includeChildren: true, selected: page);
        }
    }

    private void OnSkinColorOnValueChanged()
    {
        if (Profile is null) return;

        var skin = _prototypeManager.Index<SpeciesPrototype>(Profile.Species).SkinColoration;

        switch (skin)
        {
            case HumanoidSkinColor.HumanToned:
                {
                    if (!Skin.Visible)
                    {
                        Skin.Visible = true;
                        RgbSkinColorContainer.Visible = false;
                    }

                    var color = SkinColor.HumanSkinTone((int) Skin.Value);

                    Markings.CurrentSkinColor = color;
                    Profile = Profile.WithCharacterAppearance(Profile.Appearance.WithSkinColor(color));

                    break;
                }
            case HumanoidSkinColor.Hues:
                {
                    if (!RgbSkinColorContainer.Visible)
                    {
                        Skin.Visible = false;
                        RgbSkinColorContainer.Visible = true;
                    }

                    Markings.CurrentSkinColor = _rgbSkinColorSelector.Color;
                    Profile = Profile.WithCharacterAppearance(Profile.Appearance.WithSkinColor(_rgbSkinColorSelector.Color));
                    break;
                }
            case HumanoidSkinColor.TintedHues:
                {
                    if (!RgbSkinColorContainer.Visible)
                    {
                        Skin.Visible = false;
                        RgbSkinColorContainer.Visible = true;
                    }

                    var color = SkinColor.TintedHues(_rgbSkinColorSelector.Color);

                    Markings.CurrentSkinColor = color;
                    Profile = Profile.WithCharacterAppearance(Profile.Appearance.WithSkinColor(color));

                    break;
                }
            case HumanoidSkinColor.VoxFeathers:
                {
                    if (!RgbSkinColorContainer.Visible)
                    {
                        Skin.Visible = false;
                        RgbSkinColorContainer.Visible = true;
                    }

                    var color = SkinColor.ClosestVoxColor(_rgbSkinColorSelector.Color);

                    Markings.CurrentSkinColor = color;
                    Profile = Profile.WithCharacterAppearance(Profile.Appearance.WithSkinColor(color));

                    break;
                }
            case HumanoidSkinColor.NoColor: // Goob #1161
                {
                    if (!RgbSkinColorContainer.Visible)
                    {
                        Skin.Visible = false;
                        RgbSkinColorContainer.Visible = true;
                    }

                    var color = Color.FromName("White");

                    Markings.CurrentSkinColor = color;
                    Profile = Profile.WithCharacterAppearance(Profile.Appearance.WithSkinColor(color));
                    break;
                }
            // Goobstation Section Start - Tajaran
            case HumanoidSkinColor.AnimalFur: // Goobstation - Tajaran
                {
                    if (!RgbSkinColorContainer.Visible)
                    {
                        Skin.Visible = false;
                        RgbSkinColorContainer.Visible = true;
                    }

                    var color = SkinColor.ClosestAnimalFurColor(_rgbSkinColorSelector.Color);

                    Markings.CurrentSkinColor = color;
                    Profile = Profile.WithCharacterAppearance(Profile.Appearance.WithSkinColor(color));
                    break;
                }
            // Goobstation Section End - Tajaran
        }

        ReloadProfilePreview();
    }
}

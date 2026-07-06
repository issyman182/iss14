#nullable enable
using Content.IntegrationTests.Fixtures;
using Content.Server._EinsteinEngines.Language;
using Content.Shared._EinsteinEngines.Language;
using Content.Shared._EinsteinEngines.Language.Components;
using Content.Shared._EinsteinEngines.Language.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._EinsteinEngines;

/// <summary>
/// iss14: verifies the Einstein Engines language port - a spawned human is a language
/// speaker with a valid current language, and language obfuscation actually garbles text.
/// </summary>
[TestFixture]
public sealed class LanguageTest : GameTest
{
    private static readonly ProtoId<LanguagePrototype> TauCetiBasic = "TauCetiBasic";
    private static readonly ProtoId<LanguagePrototype> Draconic = "Draconic";

    [Test]
    public async Task HumanSpeaksAndUnderstandsALanguage()
    {
        var pair = Pair;
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        EntityUid human = default;
        await server.WaitAssertion(() => human = server.EntMan.Spawn("MobHuman", map.MapCoords));

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var language = server.System<LanguageSystem>();

            Assert.That(server.EntMan.TryGetComponent<LanguageSpeakerComponent>(human, out var speaker),
                "Spawned human has no LanguageSpeakerComponent - BaseMob/species language integration missing.");

            Assert.Multiple(() =>
            {
                Assert.That(speaker!.CurrentLanguage, Is.Not.Empty,
                    "Human has no current language set after MapInit.");
                Assert.That(speaker.SpokenLanguages, Is.Not.Empty,
                    "Human has no spoken languages - LanguageKnowledge from species did not apply.");
                Assert.That(speaker.SpokenLanguages, Does.Contain(TauCetiBasic),
                    "Human cannot speak TauCetiBasic.");

                var current = language.GetLanguage(human);
                Assert.That(current.ID, Is.EqualTo(speaker.CurrentLanguage),
                    "GetLanguage does not match the speaker's current language.");
                Assert.That(current.ID, Is.Not.EqualTo(SharedLanguageSystem.UniversalPrototype.Id),
                    "Human fell back to the Universal language - species knowledge missing.");

                Assert.That(language.CanSpeak(human, current.ID), "Human cannot speak its own current language.");
                Assert.That(language.CanUnderstand(human, current.ID), "Human cannot understand its own current language.");

                // A human knows no Draconic and should not understand it.
                Assert.That(language.CanUnderstand(human, Draconic), Is.False,
                    "Human understands Draconic despite not knowing it.");
            });
        });

        await server.WaitPost(() => server.EntMan.DeleteEntity(human));
    }

    [Test]
    public async Task ObfuscationGarblesUnknownLanguage()
    {
        var pair = Pair;
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var language = server.System<LanguageSystem>();
            var draconic = language.GetLanguagePrototype(Draconic);
            Assert.That(draconic, Is.Not.Null, "Draconic language prototype is missing.");

            const string message = "The quick brown fox jumps over the lazy dog, but nobody expects it to.";
            var obfuscated = language.ObfuscateSpeech(message, draconic!);

            Assert.Multiple(() =>
            {
                Assert.That(obfuscated, Is.Not.Empty, "Obfuscation produced an empty string.");
                Assert.That(obfuscated, Is.Not.EqualTo(message),
                    "Obfuscation returned the original message - listeners who don't know the language would understand it.");
            });

            // Obfuscation must be stable within a round: the same input yields the same output.
            var again = language.ObfuscateSpeech(message, draconic!);
            Assert.That(again, Is.EqualTo(obfuscated), "Language obfuscation is not stable within a round.");
        });
    }
}

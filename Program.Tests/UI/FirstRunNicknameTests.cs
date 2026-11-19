using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using Xunit;

namespace Minecraft.Tests;

/// <summary>
/// A launcher that has just been downloaded knows no name, and says so.
///
/// It used to invent one: an empty name became the Windows account name, and
/// failing that the literal "Player". Nobody chose either, and yet that is the
/// name a friend then sees on a shared world - which is how somebody ends up
/// playing as "PC". The name is now the player's to give, and Steam's to offer
/// only when the player starts the game without a saved nickname.
/// </summary>
public sealed class FirstRunNicknameTests
{
    [Fact]
    public void EmptyNickname_IsReadyToEditAndSave()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var controls = WindowCanvasTests.LoadWindow();
            var input = Assert.IsType<TextBox>(controls.FindName("PlayerNameTextBox"));
            var button = Assert.IsType<Button>(controls.FindName("ChangePlayerNameButton"));
            var settings = new AppSettings();
            var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            void Set(string name, object value) => typeof(MainWindow).GetField(name, fields)!.SetValue(window, value);
            void Refresh(bool enabled) => typeof(MainWindow).GetMethod("RefreshPlayerNameEditor", fields)!.Invoke(window, [enabled]);
            string LaunchNickname(SteamClientStatus status) => Assert.IsType<string>(
                typeof(MainWindow).GetMethod("GetLaunchNickname", fields)!.Invoke(window, [status]));
            Set("_settings", settings);
            Set("PlayerNameTextBox", input);
            Set("ChangePlayerNameButton", button);

            Refresh(true);
            Assert.False(input.IsReadOnly);
            Assert.Equal("Сохранить никнейм", button.Content);
            var steam = new SteamClientStatus(SteamAvailability.Ready, 76561198000000000, "Steam Player", "");
            Assert.Equal("SteamPlayer", LaunchNickname(steam));
            Assert.Empty(LaunchNickname(steam with { PersonaName = "Женя" }));
            Assert.Empty(settings.PlayerName);

            settings.PlayerName = "PlayerOne";
            Refresh(true);
            Assert.True(input.IsReadOnly);
            Assert.Equal("Поменять никнейм", button.Content);
            Assert.Equal("PlayerOne", LaunchNickname(steam));

            Set("_isEditingPlayerName", true);
            Refresh(true);
            Assert.False(input.IsReadOnly);
            Assert.Equal("Сохранить никнейм", button.Content);
            Assert.Equal("SteamPlayer", LaunchNickname(steam));
            Assert.Empty(LaunchNickname(steam with { PersonaName = "Женя" }));

            Refresh(false);
            Assert.True(input.IsReadOnly);
            Assert.False(button.IsEnabled);
            return true;
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AFreshInstall_IsGivenNoNameAtAll(string? stored)
    {
        Assert.Equal(string.Empty, LocalIdentityService.NormalizedOrNothing(stored));
    }

    [Theory]
    // The plain case, and the one this exists for: a persona with a space in it
    // is a perfectly good name once the space is gone.
    [InlineData("Shizoid", "Shizoid")]
    [InlineData("Mark Zamore", "MarkZamore")]
    [InlineData("anuvenn 💀", "anuvenn")]
    [InlineData("ASS_in", "ASS_in")]
    // Steam allows far more than sixteen characters; the game does not.
    [InlineData("a_very_long_persona_name", "a_very_long_pers")]
    public void APersona_BecomesANameTheGameCanAddress(string persona, string expected)
    {
        var name = LocalIdentityService.NicknameFromPersona(persona);

        Assert.Equal(expected, name);
        Assert.True(
            LocalIdentityService.IsNameMinecraftAccepts(name),
            $"\"{name}\" came out of a persona but the game could not address it.");
        // It also has to be a name the player can then edit in place: the field
        // refuses any draft with a letter outside the rule.
        Assert.True(LocalIdentityService.IsNicknameDraftValid(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Женя")]
    [InlineData("🙂🙂🙂")]
    [InlineData("...")]
    // Two letters is a name Minecraft will not have, and half a name is worse
    // than none: an empty field asks to be filled, a stump does not.
    [InlineData("Ab")]
    [InlineData("PC")]
    public void APersonaWithNothingUsable_IsNotTakenAtAll(string? persona)
    {
        Assert.Equal(string.Empty, LocalIdentityService.NicknameFromPersona(persona));
    }

    /// <summary>
    /// Nothing in the launcher may name a player after the computer they sit
    /// at. This is the whole of that rule, and it is one line to check.
    /// </summary>
    [Fact]
    public void NothingNamesAPlayerAfterTheirComputer()
    {
        var offenders = Directory
            .EnumerateFiles(FindRepositoryDirectory("Program"), "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("Environment.UserName", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These invent a name out of the Windows account: " + string.Join(", ", offenders));
    }

    [Fact]
    public void SteamNicknameIsCommittedByPlayBeforeStartingTheGame()
    {
        var window = File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml.cs"));
        var settling = Between(window, "private void ResolveAndPersistLocalIdentity(", "\n    }");

        Assert.DoesNotContain("NicknameFromPersona", settling, StringComparison.Ordinal);
        var play = Between(window, "private async Task PlaySelectedBuildAsync(", "\n    }");
        var choose = play.IndexOf("GetLaunchNickname(steamStatus)", StringComparison.Ordinal);
        var commit = play.IndexOf("CommitPlayerName(launchNickname)", StringComparison.Ordinal);
        var reject = play.IndexOf("if (launchNickname.Length == 0)", StringComparison.Ordinal);
        var start = play.IndexOf("launch.LaunchAsync(", StringComparison.Ordinal);
        Assert.True(choose >= 0 && choose < commit && commit < reject && reject < start);
    }

    /// <summary>
    /// A name typed while Steam is still connecting has to be written down
    /// then and there, or the launcher forgets it the moment Steam answers.
    /// </summary>
    [Fact]
    public void ANameTypedBeforeSteamAnswers_IsStillSaved()
    {
        var window = File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml.cs"));
        var body = Between(window, "private void PersistActivePlayerIdentity()", "\n    }");

        // The identity fields are filled only when Steam has bound, and the
        // save happens afterwards either way - outside that condition.
        var bound = body.IndexOf("_identityService is { IsBound: true }", StringComparison.Ordinal);
        var saved = body.LastIndexOf("_settingsService.Save(_settings);", StringComparison.Ordinal);
        Assert.True(bound >= 0, "The bound check is gone; the name may now be saved without one.");
        Assert.True(saved > bound, "The name is no longer saved after the identity check.");
        // And nothing turns back at the door for want of a name: the early
        // return above the identity check must not mention one.
        Assert.DoesNotContain("PlayerName", body[..bound], StringComparison.Ordinal);
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"\"{start}\" was not found.");
        var rest = text[from..];
        var to = rest.IndexOf(end, start.Length, StringComparison.Ordinal);
        return to < 0 ? rest : rest[..to];
    }

    private static string FindRepositoryDirectory(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException($"Repository folder was not found: {Path.Combine(relativeParts)}");
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(relativeParts)}");
    }
}

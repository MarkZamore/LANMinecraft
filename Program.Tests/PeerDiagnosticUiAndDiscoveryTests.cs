using System.Text.Json;
using System.Xml.Linq;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// What the window shows about other players, and where it shows it. Peers come
/// from Steam rich presence now, so these cases pin the view model against a
/// presence rather than against a UDP announcement.
/// </summary>
public sealed class PeerDiagnosticUiAndDiscoveryTests
{
    private const ulong PeerSteamId = 76561198000000002;

    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void PeerViewModel_AppliesAndClearsDiagnosticCapability()
    {
        var peer = NewPeer();
        peer.Apply(Presence() with { DiagnosticProtocolVersion = BugReportManifest.ProtocolVersion }, "pack-hash");

        Assert.Equal(BugReportManifest.ProtocolVersion, peer.DiagnosticProtocolVersion);
        Assert.True(peer.SupportsDiagnosticLogs);

        // A peer that stops publishing the capability (or publishes another
        // version of it) is no longer offered as a target.
        peer.Apply(Presence() with { DiagnosticProtocolVersion = 0 }, "pack-hash");

        Assert.Equal(0, peer.DiagnosticProtocolVersion);
        Assert.False(peer.SupportsDiagnosticLogs);
    }

    [Fact]
    public void PeerViewModel_ShowsBothNamesWhenTheyDiffer()
    {
        var peer = NewPeer();
        peer.Apply(Presence() with { PlayerName = "anuvenn", PersonaName = "Anu" }, "pack-hash");
        Assert.Equal("anuvenn (Anu)", peer.DisplayName);

        peer.Apply(Presence() with { PlayerName = "", PersonaName = "Anu" }, "pack-hash");
        Assert.Equal("Anu", peer.DisplayName);

        peer.Apply(Presence() with { PlayerName = "anuvenn", PersonaName = "anuvenn" }, "pack-hash");
        Assert.Equal("anuvenn", peer.DisplayName);
    }

    [Fact]
    public void PeerViewModel_ReportsWhetherThePackMatches()
    {
        var peer = NewPeer();
        peer.Apply(Presence() with { PackHash = "abc" }, "abc");
        Assert.Equal("сборка совпадает", peer.PackStatus);

        peer.Apply(Presence() with { PackHash = "other" }, "abc");
        Assert.Equal("другая сборка", peer.PackStatus);
    }

    [Fact]
    public void DiagnosticTargetNobody_IsTheNonPersistentDefaultSentinel()
    {
        var nobody = DiagnosticLogTargetOption.Nobody;

        Assert.True(nobody.IsNobody);
        Assert.False(nobody.SteamId.IsValid);
        Assert.Equal("Никому", nobody.DisplayName);

        var settingsJson = JsonSerializer.Serialize(new AppSettings(), WebJson);
        Assert.DoesNotContain(
            "diagnosticLogTarget",
            settingsJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(AppSettings).GetProperties(),
            property => string.Equals(
                property.Name,
                "DiagnosticLogTargetIdentityId",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// The settings file no longer carries an IP choice; an old file that still
    /// has one must load without complaint and simply drop it.
    /// </summary>
    [Fact]
    public void SettingsHaveNoNetworkSelectionLeft()
    {
        Assert.DoesNotContain(
            typeof(AppSettings).GetProperties(),
            property => property.Name.Contains("Network", StringComparison.Ordinal));

        var legacy = """
        {
          "playerName": "MarkZamore",
          "selectedNetworkInterfaceId": "software-interface",
          "selectedNetworkAddress": "10.0.0.2"
        }
        """;
        var settings = JsonSerializer.Deserialize<AppSettings>(legacy, WebJson);
        Assert.NotNull(settings);
        Assert.Equal("MarkZamore", settings.PlayerName);
    }

    [Fact]
    public void MainWindowXaml_HostsTheDiagnosticsPanelInItsOwnColumn()
    {
        var xamlPath = FindRepositoryFile("Program", "MainWindow.xaml");
        var document = XDocument.Load(xamlPath);
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        var title = document
            .Descendants(presentation + "TextBlock")
            .SingleOrDefault(element => (string?)element.Attribute("Text") == "Сообщить о проблеме");
        Assert.NotNull(title);
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "Кому отправить");
        Assert.Contains(
            document.Descendants(),
            element =>
                element.Name.LocalName == "CenteredDropDown" &&
                (string?)element.Attribute(x + "Name") == "DiagnosticLogTargetComboBox");
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute(x + "Name") == "DiagnosticLogStatusText");
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element =>
                (string?)element.Attribute(x + "Name") == "SendBugReportButton" &&
                (string?)element.Attribute("Content") == "Отправить отчёт");
        Assert.Contains(
            document.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute(x + "Name") == "BugReportMessageTextBox");

        // The panel owns the right-hand column, so it never competes for space
        // with the play/pack controls.
        var panel = title.Parent;
        Assert.NotNull(panel);
        Assert.Equal("2", (string?)panel.Attribute("Grid.Column"));
    }

    /// <summary>The IP picker is gone from the window, not merely hidden.</summary>
    [Fact]
    public void MainWindowXaml_HasNoNetworkAddressPicker()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml"));

        Assert.DoesNotContain("NetworkAddress", xaml, StringComparison.Ordinal);
        Assert.Contains("SteamStatusText", xaml, StringComparison.Ordinal);
    }

    private static PeerViewModel NewPeer()
    {
        Assert.True(SteamId64.TryFrom(PeerSteamId, out var steamId));
        return new PeerViewModel { SteamId = steamId };
    }

    private static SteamPeerPresence Presence()
    {
        Assert.True(SteamId64.TryFrom(PeerSteamId, out var steamId));
        return new SteamPeerPresence
        {
            SteamId = steamId,
            PersonaName = "Remote",
            ProtocolVersion = SteamPresenceCodec.ProtocolVersion,
            PlayerName = "Remote",
            MinecraftUuid = Guid.NewGuid().ToString("D"),
            PackHash = "pack-hash"
        };
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(
                current.FullName,
                Path.Combine);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Repository file was not found: {Path.Combine(relativeParts)}");
    }
}

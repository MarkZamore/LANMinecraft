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

    /// <summary>
    /// Who a report goes to is a choice for right now, not a setting: the list
    /// is whoever is online at that moment, so nothing about it is written down.
    /// </summary>
    [Fact]
    public void DiagnosticTarget_IsNeverPersisted()
    {
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
    public void MainWindowXaml_HostsTheBugReportBlockBetweenTransferAndUpdates()
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
        // Nothing in the panel explains Steam or offers a folder to browse:
        // the player picks a friend, says what happened, and presses send.
        Assert.DoesNotContain(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute(x + "Name") == "OpenSupportLogsButton");
        Assert.DoesNotContain(
            document.Descendants(presentation + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains("через Steam", StringComparison.Ordinal) == true);
        Assert.Contains(
            document.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute(x + "Name") == "BugReportMessageTextBox");

        // The block sits with the things a player does: after sending a world,
        // before the bars that show what the launcher itself is doing.
        var panel = title.Parent;
        Assert.NotNull(panel);
        Assert.Equal("0", (string?)panel.Attribute("Grid.Column"));
        Assert.Equal("2", (string?)panel.Attribute("Grid.ColumnSpan"));
        var transferRow = RowOf(document.Descendants(presentation + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "TransferButton"));
        var updateRow = RowOf(document.Descendants(presentation + "ProgressBar")
            .Single(element => (string?)element.Attribute(x + "Name") == "UpdateProgressBar"));
        var reportRow = RowOf(panel);
        Assert.True(
            transferRow < reportRow && reportRow < updateRow,
            $"transfer row {transferRow}, report row {reportRow}, update row {updateRow}");
    }

    /// <summary>
    /// The right column is the version history: one list, scrollable, in the
    /// place the bug report used to occupy. Steam is no longer a panel there.
    /// </summary>
    [Fact]
    public void MainWindowXaml_HostsTheChangelogInTheRightColumn()
    {
        var document = XDocument.Load(FindRepositoryFile("Program", "MainWindow.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        var title = document
            .Descendants(presentation + "TextBlock")
            .SingleOrDefault(element => (string?)element.Attribute("Text") == "Что нового");
        Assert.NotNull(title);
        Assert.Equal("2", (string?)title.Parent?.Attribute("Grid.Column"));

        var list = document
            .Descendants(presentation + "ItemsControl")
            .SingleOrDefault(element => (string?)element.Attribute(x + "Name") == "ChangelogList");
        Assert.NotNull(list);
        Assert.NotEmpty(list.Ancestors(presentation + "ScrollViewer"));

        Assert.DoesNotContain(
            document.Descendants(),
            element => (string?)element.Attribute(x + "Name") is "SteamProblemPanel" or "SteamStatusText");
        // Steam is one spot in the footer: the name, or the button that retries.
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute(x + "Name") == "SteamPersonaText");
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element =>
                (string?)element.Attribute(x + "Name") == "RetrySteamButton" &&
                (string?)element.Attribute("Content") == "Повторить");
    }

    /// <summary>The Grid.Row of the nearest element that declares one.</summary>
    private static int RowOf(XElement element) =>
        int.Parse(
            (string)element.AncestorsAndSelf().First(candidate => candidate.Attribute("Grid.Row") is not null)
                .Attribute("Grid.Row")!,
            System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The IP picker is gone from the window, not merely hidden.</summary>
    [Fact]
    public void MainWindowXaml_HasNoNetworkAddressPicker()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml"));

        Assert.DoesNotContain("NetworkAddress", xaml, StringComparison.Ordinal);
        Assert.Contains("RetrySteamButton", xaml, StringComparison.Ordinal);
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

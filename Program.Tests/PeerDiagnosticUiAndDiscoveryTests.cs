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
        Assert.Equal("anuvenn (Anu)", peer.PeerName);

        peer.Apply(Presence() with { PlayerName = "", PersonaName = "Anu" }, "pack-hash");
        Assert.Equal("Anu", peer.PeerName);

        peer.Apply(Presence() with { PlayerName = "anuvenn", PersonaName = "anuvenn" }, "pack-hash");
        Assert.Equal("anuvenn", peer.PeerName);
    }

    /// <summary>
    /// And the line the list shows is the name and where they are, because the
    /// list is read to decide who a world can be sent to.
    /// </summary>
    [Fact]
    public void PeerViewModel_SaysWhereTheyAre()
    {
        var peer = NewPeer();

        peer.Apply(Presence() with { PlayerName = "anuvenn", PersonaName = "Anu", Release = 312 }, "pack-hash");
        Assert.Equal("В лаунчере 312", peer.StatusText);
        Assert.Equal("anuvenn (Anu) - В лаунчере 312", peer.DisplayName);

        // In a build: the folder is its name, and it is only worth saying once
        // the game is actually up.
        peer.Apply(
            Presence() with
            {
                PlayerName = "anuvenn",
                PersonaName = "Anu",
                PackName = "Build A",
                Release = 312,
                State = SteamPresenceCodec.StateInGame
            },
            "pack-hash");
        Assert.Equal("Build A на 312", peer.StatusText);

        // A launcher older than these fields names neither, and is still in the
        // launcher rather than nowhere. A peer of its own, because a number
        // once heard is never unlearnt - see the test below for why.
        var older = NewPeer();
        older.Apply(
            Presence() with { PlayerName = "anuvenn", State = SteamPresenceCodec.StateInGame },
            "pack-hash");
        Assert.Equal("В лаунчере", older.StatusText);
    }

    /// <summary>
    /// Closing the launcher and carrying on playing does not move a player to
    /// another game, and does not take the launcher's number off them. Neither
    /// stopped being true; only their answering did.
    /// </summary>
    [Fact]
    public void PeerViewModel_KeepsTheBuildAndTheNumberWhenTheLauncherGoes()
    {
        var peer = NewPeer();
        peer.Apply(
            Presence() with
            {
                PlayerName = "anuvenn",
                PackName = "Build A",
                Release = 312,
                State = SteamPresenceCodec.StateInGame
            },
            "pack-hash");
        Assert.Equal("Build A на 312", peer.StatusText);

        // The launcher is gone; the directory hands back what they last said.
        peer.Apply(
            new SteamPeerPresence
            {
                SteamId = peer.SteamId,
                PersonaName = "Anu",
                IsOutsideLauncher = true,
                PackName = "Build A",
                Release = 312
            },
            "pack-hash");

        Assert.Equal("Build A на 312", peer.StatusText);
        Assert.False(peer.IsCompatible);
    }

    /// <summary>
    /// Somebody in Minecraft without this launcher is listed and said to be
    /// elsewhere - and a world cannot be sent to them, which is the whole
    /// reason the list says it.
    /// </summary>
    [Fact]
    public void PeerViewModel_SaysWhenSomebodyIsInAnotherGame()
    {
        var peer = NewPeer();

        peer.Apply(
            new SteamPeerPresence
            {
                SteamId = peer.SteamId,
                PersonaName = "Anu",
                IsOutsideLauncher = true
            },
            "pack-hash");

        Assert.Equal("В другой игре", peer.StatusText);
        Assert.Equal("Anu - В другой игре", peer.DisplayName);
        Assert.False(peer.IsCompatible);
        Assert.False(peer.SupportsDiagnosticLogs);
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
        // A field, not a label: the launcher's own line can be selected and
        // scrolled, and looks like the box above it.
        Assert.Contains(
            document.Descendants(presentation + "TextBox"),
            element =>
                (string?)element.Attribute(x + "Name") == "DiagnosticLogStatusText" &&
                (string?)element.Attribute("IsReadOnly") == "True");
        var status = document.Descendants(presentation + "TextBox")
            .Single(element => (string?)element.Attribute(x + "Name") == "DiagnosticLogStatusText");
        Assert.Empty(status.Ancestors(presentation + "Expander"));
        var statusArea = status.Parent;
        Assert.Equal("DiagnosticStatusArea", (string?)statusArea?.Attribute(x + "Name"));
        Assert.Equal("MainPanel", (string?)statusArea?.Parent?.Attribute(x + "Name"));
        Assert.Null(status.Attribute("Grid.Column"));
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element =>
                (string?)element.Attribute(x + "Name") == "SendBugReportButton" &&
                (string?)element.Attribute("Content") == "Отправить");
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
        var panel = document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "ReportPanel");
        var reportSection = title.Ancestors(presentation + "Expander").Single();
        Assert.Equal("ReportExpander", (string?)reportSection.Attribute(x + "Name"));
        Assert.Contains(reportSection, panel.Ancestors());
        var transferRow = RowOf(document.Descendants(presentation + "Expander")
            .Single(element => (string?)element.Attribute(x + "Name") == "WorldTransferExpander"));
        var settings = document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "SettingsGrid");
        Assert.True(RowOf(settings) < RowOf(statusArea!));
        Assert.True(RowOf(statusArea!) < transferRow);
        var reportRow = RowOf(reportSection);
        Assert.True(transferRow < reportRow,
            $"transfer row {transferRow}, report row {reportRow}");
        var body = document.Descendants(presentation + "ScrollViewer")
            .Single(element => (string?)element.Attribute(x + "Name") == "BodyScroll");
        Assert.Contains(body, panel.Ancestors());
        var footer = document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "FooterGrid");
        var update = document.Descendants(presentation + "ProgressBar")
            .Single(element => (string?)element.Attribute(x + "Name") == "UpdateProgressBar");
        var updateArea = document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "UpdateArea");
        Assert.DoesNotContain(footer, update.Ancestors());
        Assert.Equal("RootGrid", (string?)updateArea.Parent?.Attribute(x + "Name"));
        Assert.True(RowOf(body) < RowOf(updateArea));
        Assert.True(RowOf(updateArea) < RowOf(footer));
    }

    /// <summary>
    /// The right column holds two faces in one place: the assistant it opens on
    /// and the version history behind it. One list, scrollable; one heading that
    /// names whichever is showing; one button in the footer that swaps them and
    /// carries the name of the other face. Steam is no longer a panel there.
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
            .SingleOrDefault(element => (string?)element.Attribute(x + "Name") == "SidePanelTitle");
        Assert.NotNull(title);
        var sideSection = title.Ancestors(presentation + "Border")
            .Single(element => (string?)element.Attribute(x + "Name") == "SidePanelCard");
        Assert.Empty(title.Ancestors(presentation + "Expander"));
        Assert.Equal("{StaticResource Font.SizeHeading}", (string?)title.Attribute("FontSize"));
        // The launcher opens on the assistant, so that is the heading it wears.
        Assert.Equal("AI-помощник", (string?)title.Attribute("Text"));

        var list = document
            .Descendants(presentation + "ItemsControl")
            .SingleOrDefault(element => (string?)element.Attribute(x + "Name") == "ChangelogList");
        Assert.NotNull(list);
        Assert.NotEmpty(list.Ancestors(presentation + "ScrollViewer"));

        // Two faces, and the one that is not showing says so in the markup:
        // whatever the launcher does later, it starts on the assistant.
        var news = document
            .Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "ChangelogPanel");
        var chat = document
            .Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "ChatPanel");
        Assert.Equal("Collapsed", (string?)news.Attribute("Visibility"));
        Assert.Null(chat.Attribute("Visibility"));
        Assert.Contains(sideSection, news.Ancestors());
        Assert.Contains(sideSection, chat.Ancestors());

        // The transport is unavailable, but a draft remains editable and scrollable.
        var field = document
            .Descendants(presentation + "TextBox")
            .Single(element => (string?)element.Attribute(x + "Name") == "ChatMessageTextBox");
        Assert.Null(field.Attribute("IsEnabled"));
        Assert.Null(field.Attribute("IsReadOnly"));
        Assert.Equal("True", (string?)field.Attribute("AcceptsReturn"));
        Assert.Equal("Wrap", (string?)field.Attribute("TextWrapping"));
        Assert.Equal("Auto", (string?)field.Attribute("VerticalScrollBarVisibility"));
        var send = document
            .Descendants(presentation + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "ChatSendButton");
        Assert.Equal("{Binding IsActionEnabled, ElementName=ChatComposerControl}", (string?)send.Attribute("IsEnabled"));
        Assert.Equal("{StaticResource ChatActionButtonStyle}", (string?)send.Attribute("Style"));
        var composer = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "ChatComposerControl");
        Assert.Equal("False", (string?)composer.Attribute("IsServiceAvailable"));
        Assert.Contains(composer, field.Ancestors());
        Assert.Contains(composer, send.Ancestors());

        // The switch stands where the version number used to, and is named for
        // what pressing it brings rather than for what is on screen.
        var toggle = document
            .Descendants(presentation + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "SidePanelToggleButton");
        Assert.Equal("Новости", (string?)toggle.Attribute("Content"));
        Assert.Equal("SidePanelToggleButton_Click", (string?)toggle.Attribute("Click"));
        Assert.DoesNotContain(
            document.Descendants(),
            element => (string?)element.Attribute(x + "Name") == "VersionTextBlock");

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

    /// <summary>
    /// Preparing the game is shown inside the button that started it: a fill and
    /// a caption in the Play button, and no separate bar anywhere in the window.
    /// </summary>
    [Fact]
    public void MainWindowXaml_ShowsPreparationInsideThePlayButton()
    {
        var xamlPath = FindRepositoryFile("Program", "MainWindow.xaml");
        var document = XDocument.Load(xamlPath);
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        var play = document
            .Descendants(presentation + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "PlayButton");
        var bar = play
            .Descendants(presentation + "ProgressBar")
            .SingleOrDefault(element => (string?)element.Attribute(x + "Name") == "PlayProgressBar");
        Assert.NotNull(bar);
        // Hidden until there is something to report, and drawn under the caption.
        Assert.Equal("Collapsed", (string?)bar.Attribute("Visibility"));
        Assert.Contains(
            play.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute(x + "Name") == "PlayButtonText");

        var xaml = File.ReadAllText(xamlPath);
        Assert.DoesNotContain("RuntimeProgress", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two action sections start closed while the sidebar stays visible in its own card.
    /// </summary>
    [Fact]
    public void MainWindowXaml_KeepsTwoColumnsTwoCollapsedSectionsAndPersistentSidebar()
    {
        var document = XDocument.Load(FindRepositoryFile("Program", "MainWindow.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        var root = document
            .Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "RootGrid");
        Assert.Null(root.Attribute("MaxWidth"));
        var columns = root.Element(presentation + "Grid.ColumnDefinitions")!.Elements().ToArray();
        Assert.Equal(new[] { "2*", "{StaticResource Size.ColumnGap}", "1*" },
            columns.Select(column => (string?)column.Attribute("Width")));
        Assert.All(columns, column =>
        {
            Assert.Null(column.Attribute("MinWidth"));
            Assert.Null(column.Attribute("MaxWidth"));
        });
        var rows = root
            .Elements(presentation + "Grid.RowDefinitions")
            .Single()
            .Elements()
            .Select(row => (string?)row.Attribute("Height"))
            .ToArray();
        Assert.Equal(new[] { "Auto", "*", "Auto", "Auto" }, rows);
        var body = document.Descendants(presentation + "ScrollViewer")
            .Single(element => (string?)element.Attribute(x + "Name") == "BodyScroll");
        Assert.Equal("{StaticResource LauncherBodyScrollViewerStyle}", (string?)body.Attribute("Style"));
        var bodyBar = document.Descendants(presentation + "ScrollBar")
            .Single(element => (string?)element.Attribute(x + "Name") == "BodyScrollBar");
        Assert.Equal("0", (string?)bodyBar.Attribute("Grid.Row"));
        Assert.Equal("2", (string?)bodyBar.Attribute("Grid.RowSpan"));
        Assert.Equal("{StaticResource Gap.Below4}", (string?)bodyBar.Attribute("Margin"));
        Assert.Equal(2, document.Descendants(presentation + "Expander").Count());
        foreach (var name in new[] { "WorldTransferExpander", "ReportExpander" })
        {
            var section = document.Descendants(presentation + "Expander")
                .Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.Equal("False", (string?)section.Attribute("IsExpanded"));
        }
        var side = document.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(x + "Name") == "SidePanelCard");
        Assert.Equal("2", (string?)side.Attribute("Grid.Column"));
        Assert.Equal("3", (string?)side.Attribute("Grid.RowSpan"));
        var updateArea = document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "UpdateArea");
        Assert.Equal("2", (string?)updateArea.Attribute("Grid.Row"));
        Assert.Equal((string?)updateArea.Attribute("Margin"), (string?)side.Attribute("Margin"));
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(x + "Name") is "WideSideHost" or "InlineSideHost" or "SidePanelExpander");
        Assert.Equal("3", (string?)document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "FooterGrid")
            .Attribute("Grid.Row"));
        foreach (var name in new[] { "SidePanel", "ChatPanel", "ChangelogPanel", "ChangelogList", "ChatMessageTextBox", "ChatSendButton" })
        {
            Assert.Single(document.Descendants(), element => (string?)element.Attribute(x + "Name") == name);
        }
    }

    /// <summary>
    /// The settings under the Play button are two halves of equal width: who
    /// you are and what you play on the left, how it looks and what it may use
    /// on the right. The preset button is the right half's second row, and it
    /// explains itself when it is off, so ShowOnDisabled is part of it.
    /// </summary>
    [Fact]
    public void MainWindowXaml_SplitsTheSettingsIntoTwoEqualHalves()
    {
        var document = XDocument.Load(FindRepositoryFile("Program", "MainWindow.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        var settings = document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "SettingsGrid");
        var widths = settings
            .Elements(presentation + "Grid.ColumnDefinitions")
            .Single()
            .Elements()
            .Select(column => (string?)column.Attribute("Width"))
            .ToArray();
        Assert.Equal(["1*", "1*"], widths.Select(width => width ?? string.Empty).ToArray());

        // Which half a control lives in: the cell of whichever ancestor is a
        // direct child of the halves grid.
        (int Row, int Column) Cell(XElement element)
        {
            var cell = element.AncestorsAndSelf().First(candidate => candidate.Parent == settings);
            return (
                int.Parse((string?)cell.Attribute("Grid.Row") ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                int.Parse((string?)cell.Attribute("Grid.Column") ?? "0", System.Globalization.CultureInfo.InvariantCulture));
        }

        var name = Cell(document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "PlayerNameTextBox"));
        var build = Cell(document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "BuildComboBox"));
        var skin = Cell(document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "SkinButton"));
        var memory = Cell(document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "MemoryTextBox"));
        var button = document.Descendants(presentation + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "ControlsPresetButton");
        var preset = Cell(button);

        // Left half: the name, then the build under it.
        Assert.Equal((0, 0), name);
        Assert.Equal((1, 0), build);
        // Right half: the skin and the memory box share a row, the preset is under them.
        Assert.Equal((0, 1), skin);
        Assert.Equal((0, 1), memory);
        Assert.Equal((1, 1), preset);

        Assert.Equal("Пресет управления", (string?)button.Attribute("Content"));
        Assert.Equal("True", (string?)button.Attribute("ToolTipService.ShowOnDisabled"));

        // The row reads left to right: what this does to the build, what the
        // build weighs, and what can be written into it. The preset takes
        // whatever the other two leave, so there is no gap in the middle.
        var delete = document.Descendants(presentation + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "DeleteBuildButton");
        var estimate = document.Descendants(presentation + "TextBlock")
            .Single(element => (string?)element.Attribute(x + "Name") == "MemoryEstimateText");
        Assert.Equal((1, 1), Cell(delete));
        Assert.Equal("0", (string?)delete.Attribute("Grid.Column"));
        Assert.Equal("1", (string?)estimate.Attribute("Grid.Column"));
        Assert.Equal("2", (string?)button.Attribute("Grid.Column"));
        // The gap between the three is the one the transfer and report rows
        // use. The first of them needs none: the row already has its own.
        Assert.Null(delete.Attribute("Margin"));
        Assert.Equal("{StaticResource Gap.Left2}", (string?)estimate.Attribute("Margin"));
        Assert.Equal("{StaticResource Gap.Left2}", (string?)button.Attribute("Margin"));
        Assert.Equal("{StaticResource IconActionButton}", (string?)delete.Attribute("Style"));
        Assert.Null(delete.Attribute("Width"));
        var deleteGlyph = Assert.Single(delete.Elements(presentation + "TextBlock"));
        Assert.Equal("\uE74D", (string?)deleteGlyph.Attribute("Text"));
        Assert.Equal("{StaticResource Font.Icons}", (string?)deleteGlyph.Attribute("FontFamily"));
        Assert.Equal("{StaticResource Size.Icon}", (string?)deleteGlyph.Attribute("FontSize"));
        Assert.Null(delete.Attribute("Foreground"));
        Assert.Equal("True", (string?)delete.Attribute("ToolTipService.ShowOnDisabled"));
        // Destructive actions share the reference design's neutral icon-button surface.
        Assert.Null(delete.Attribute("Background"));
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

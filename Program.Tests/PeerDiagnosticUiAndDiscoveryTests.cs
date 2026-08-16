using System.Text.Json;
using System.Xml.Linq;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PeerDiagnosticUiAndDiscoveryTests
{
    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void DiscoveryV6_DiagnosticCapabilityIsAdditiveAndRoundTrips()
    {
        var fingerprint = new string('A', 64);
        var announcement = new PeerAnnouncement
        {
            App = "MinecraftPortable",
            ProtocolVersion = PeerDiscoveryService.ProtocolVersion,
            IdentityId = Guid.NewGuid().ToString("D"),
            PlayerName = "Remote Player",
            PackHash = "pack-hash",
            DiagnosticLogProtocolVersion = PeerSupportProtocol.ProtocolVersion,
            DiagnosticTlsFingerprint = fingerprint
        };

        var json = JsonSerializer.Serialize(announcement, WebJson);
        var restored = JsonSerializer.Deserialize<PeerAnnouncement>(json, WebJson);

        Assert.NotNull(restored);
        Assert.Equal(6, restored.ProtocolVersion);
        Assert.Equal(
            PeerSupportProtocol.ProtocolVersion,
            restored.DiagnosticLogProtocolVersion);
        Assert.Equal(fingerprint, restored.DiagnosticTlsFingerprint);
        Assert.Contains(
            "\"diagnosticLogProtocolVersion\":1",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            $"\"diagnosticTlsFingerprint\":\"{fingerprint}\"",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Version21ShapedDiscovery_RemainsAcceptedButHasNoDiagnosticCapability()
    {
        var identityId = Guid.NewGuid().ToString("D");
        var json = $$"""
        {
          "app": "MinecraftPortable",
          "protocolVersion": 6,
          "playerName": "Version 21",
          "identityId": "{{identityId}}",
          "identityName": "Version 21",
          "isHost": true,
          "packHash": "pack-hash",
          "serverPort": 35656,
          "lanSessionId": "legacy-session",
          "lanWorldName": "Legacy world"
        }
        """;

        var announcement = JsonSerializer.Deserialize<PeerAnnouncement>(
            json,
            WebJson);

        Assert.NotNull(announcement);
        Assert.Equal(PeerDiscoveryService.ProtocolVersion, announcement.ProtocolVersion);
        Assert.Equal(0, announcement.DiagnosticLogProtocolVersion);
        Assert.Empty(announcement.DiagnosticTlsFingerprint);

        var peer = new PeerViewModel();
        peer.Apply(announcement, "pack-hash");

        Assert.Equal(identityId, peer.IdentityId);
        Assert.False(peer.SupportsDiagnosticLogs);
    }

    [Fact]
    public void PeerViewModel_AppliesAndClearsDiagnosticCapability()
    {
        var peer = new PeerViewModel();
        var fingerprint = string.Concat(Enumerable.Repeat("01", 32));
        peer.Apply(
            new PeerAnnouncement
            {
                ProtocolVersion = PeerDiscoveryService.ProtocolVersion,
                IdentityId = Guid.NewGuid().ToString("D"),
                PlayerName = "Compatible",
                DiagnosticLogProtocolVersion = PeerSupportProtocol.ProtocolVersion,
                DiagnosticTlsFingerprint = fingerprint
            },
            "pack-hash");

        Assert.Equal(
            PeerSupportProtocol.ProtocolVersion,
            peer.DiagnosticLogProtocolVersion);
        Assert.Equal(fingerprint, peer.DiagnosticTlsFingerprint);
        Assert.True(peer.SupportsDiagnosticLogs);

        peer.Apply(
            new PeerAnnouncement
            {
                ProtocolVersion = PeerDiscoveryService.ProtocolVersion,
                IdentityId = peer.IdentityId,
                PlayerName = "Version 21"
            },
            "pack-hash");

        Assert.Equal(0, peer.DiagnosticLogProtocolVersion);
        Assert.Empty(peer.DiagnosticTlsFingerprint);
        Assert.False(peer.SupportsDiagnosticLogs);
    }

    [Fact]
    public void DiagnosticTargetNobody_IsTheNonPersistentDefaultSentinel()
    {
        var nobody = DiagnosticLogTargetOption.Nobody;

        Assert.True(nobody.IsNobody);
        Assert.Empty(nobody.IdentityId);
        Assert.Equal("Никому", nobody.DisplayName);
        Assert.Empty(nobody.NetworkAddress);
        Assert.Empty(nobody.TlsFingerprint);

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

    [Fact]
    public void PeerViewModel_ObservedRemoteAddressMatchesSelectedLocalEndpoint()
    {
        var peer = new PeerViewModel();
        var identityId = Guid.NewGuid().ToString("D");
        peer.Apply(
            new PeerAnnouncement
            {
                IdentityId = identityId,
                PlayerName = "Remote",
                NetworkAddress = "10.10.0.25",
                LocalAddress = "10.10.0.10",
                LocalInterfaceId = "interface-a"
            },
            "pack-hash");
        peer.Apply(
            new PeerAnnouncement
            {
                IdentityId = identityId,
                PlayerName = "Remote",
                NetworkAddress = "10.20.0.25",
                LocalAddress = "10.20.0.10",
                LocalInterfaceId = "interface-b"
            },
            "pack-hash");
        var cutoff = DateTimeOffset.Now - TimeSpan.FromMinutes(1);

        Assert.True(peer.TryGetObservedRemoteAddress(
            "10.10.0.10",
            "INTERFACE-A",
            cutoff,
            out var firstAddress));
        Assert.Equal("10.10.0.25", firstAddress);
        Assert.True(peer.TryGetObservedRemoteAddress(
            "10.20.0.10",
            "interface-b",
            cutoff,
            out var secondAddress));
        Assert.Equal("10.20.0.25", secondAddress);
        Assert.False(peer.TryGetObservedRemoteAddress(
            "10.30.0.10",
            "interface-a",
            cutoff,
            out _));
    }

    [Fact]
    public void PeerViewModel_ObservedRemoteAddressRejectsExpiredAndLoopbackRoutes()
    {
        var peer = new PeerViewModel();
        peer.Apply(
            new PeerAnnouncement
            {
                IdentityId = Guid.NewGuid().ToString("D"),
                PlayerName = "Remote",
                NetworkAddress = "10.10.0.25",
                LocalAddress = "10.10.0.10",
                LocalInterfaceId = "interface-a"
            },
            "pack-hash");

        Assert.False(peer.TryGetObservedRemoteAddress(
            "10.10.0.10",
            "interface-a",
            DateTimeOffset.Now + TimeSpan.FromSeconds(1),
            out _));
        Assert.False(peer.TryGetObservedRemoteAddress(
            "127.0.0.1",
            "interface-a",
            DateTimeOffset.Now - TimeSpan.FromMinutes(1),
            out _));
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
            .SingleOrDefault(element => (string?)element.Attribute("Text") == "Диагностика");
        Assert.NotNull(title);
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "Передавать логи");
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
                (string?)element.Attribute(x + "Name") == "OpenSupportLogsButton" &&
                (string?)element.Attribute("Content") == "Открыть полученные логи");

        // The panel owns the right-hand column, so it never competes for space
        // with the play/pack controls.
        var panel = title.Parent;
        Assert.NotNull(panel);
        Assert.Equal("2", (string?)panel.Attribute("Grid.Column"));
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

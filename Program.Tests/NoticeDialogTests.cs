using System.Xml.Linq;

namespace Minecraft.Tests;

/// <summary>
/// The window that says something did not work, pinned where it was asked for.
/// </summary>
public class NoticeDialogTests
{
    private static XDocument Markup() =>
        XDocument.Load(Path.Combine(RepositoryRoot(), "Program", "NoticeDialog.xaml"));

    /// <summary>
    /// The title bar says the launcher's name. "Лаунчер" is what a window is,
    /// not what it is called, and the taskbar shows this beside every other
    /// program the player has open.
    /// </summary>
    [Fact]
    public void TheTitle_IsTheLaunchersName()
    {
        Assert.Equal("LANMinecraft", (string?)Markup().Root!.Attribute("Title"));
    }

    /// <summary>
    /// And it carries no button. Nothing in it is a decision - the thing has
    /// already happened and this only says what it was - so it is dismissed the
    /// way every other window is, by closing it. Esc and Enter do the same,
    /// which is what the button's IsCancel used to answer for.
    /// </summary>
    [Fact]
    public void ItCarriesNoButton()
    {
        var buttons = Markup().Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .ToArray();
        Assert.True(buttons.Length == 0, $"NoticeDialog has {buttons.Length} button(s); it is dismissed by closing it");
        Assert.Equal("Window_PreviewKeyDown", (string?)Markup().Root!.Attribute("PreviewKeyDown"));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

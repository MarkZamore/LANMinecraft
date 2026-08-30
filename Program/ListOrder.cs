namespace Minecraft;

/// <summary>
/// The order the window lists things in, written once because it is the same
/// order twice over.
///
/// The players a world can be handed to and the players a report can be sent to
/// are the same friends in two drop-downs a few rows apart, and they were
/// ordered differently: one was bound straight to the collection and read in
/// whatever order Steam answered in, the other was sorted by name. Two orders
/// for one set of people is a way to pick the wrong one.
///
/// The builds had the same shape of problem: the ones already downloaded were
/// sorted and the ones only offered were appended after them, so the list read
/// out of order and a fresh install landed on whichever build happened to be
/// written down first in the launcher's own list.
/// </summary>
public static class ListOrder
{
    /// <summary>
    /// How a name is compared: by what a person reading it would expect, not by
    /// its bytes. "Ярослав" belongs after "anuvenn" for a Russian reader and
    /// before it for an ordinal one.
    /// </summary>
    public static StringComparer Names { get; } = StringComparer.CurrentCultureIgnoreCase;

    /// <summary>
    /// Players by name, and by Steam id where two of them share one - so that
    /// a list of the same people never comes out in two different orders.
    /// </summary>
    public static IEnumerable<PeerViewModel> Players(IEnumerable<PeerViewModel> peers) =>
        peers.OrderBy(peer => peer.DisplayName, Names).ThenBy(peer => peer.SteamId.Value);

    /// <summary>
    /// Builds by name, downloaded or merely offered. The name is the folder's,
    /// not the row's: a build that is only offered carries a star in the row
    /// and it is not part of what it is called.
    /// </summary>
    public static IEnumerable<ClientBuildViewModel> Builds(IEnumerable<ClientBuildViewModel> builds) =>
        builds.OrderBy(build => build.RelativePath, Names);
}

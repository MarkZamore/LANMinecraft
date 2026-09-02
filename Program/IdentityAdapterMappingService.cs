using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Minecraft;

internal sealed class IdentityAdapterMappingService
{
    private const string XaeroWaypointTeleport = "xaero/hud/minimap/waypoint/WaypointTeleport";
    private const string FtbWaypointRowPanel =
        "dev/ftb/mods/ftbchunks/client/gui/WaypointEditorScreen$RowPanel";
    private const string FtbWaypointMapIcon = "dev/ftb/mods/ftbchunks/client/mapicon/WaypointMapIcon";
    private const string FtbTeleportFromMapPacket = "dev/ftb/mods/ftbchunks/net/TeleportFromMapPacket";
    private const string SolarFluxResourcePack = "org/zeith/solarflux/client/SolarFluxResourcePack";
    // The two classes in this pack that build an Applied Energistics menu with
    // the builder that registers it, from inside a NeoForge DeferredRegister
    // that registers it as well. Named here rather than discovered because a
    // mod class cannot be resolved from the game's mappings, and the cost of
    // naming one that is not installed is nothing: the transformer only ever
    // sees a class the game actually loads.
    private const string Ae2AddonLibMenuRegistry = "net/pedroksl/ae2addonlib/registry/MenuRegistry";
    private const string UfoModMenus = "com/raishxn/ufo/init/ModMenus";
    private const string Ae2MenuTypeBuilder = "appeng/menu/implementations/MenuTypeBuilder";
    private const string Ae2MenuBuildDescriptor =
        "(Lnet/minecraft/resources/ResourceLocation;)Lnet/minecraft/world/inventory/MenuType;";
    private const string LoginListener = "net/minecraft/server/network/ServerLoginPacketListenerImpl";
    private const string GameListener = "net/minecraft/server/network/ServerGamePacketListenerImpl";
    // The half of the listener that 1.20.2 split out and shared with the
    // configuration phase. Before that release there is no such class and the
    // method this feature reads sits on the listener itself.
    private const string CommonListener = "net/minecraft/server/network/ServerCommonPacketListenerImpl";
    private const string MovePlayerPacket =
        "net/minecraft/network/protocol/game/ServerboundMovePlayerPacket";
    private const string MoveVehiclePacket =
        "net/minecraft/network/protocol/game/ServerboundMoveVehiclePacket";
    private const string HelloPacket = "net/minecraft/network/protocol/login/ServerboundHelloPacket";
    private const string MinecraftServer = "net/minecraft/server/MinecraftServer";
    private const string Connection = "net/minecraft/network/Connection";
    private const string PlayerList = "net/minecraft/server/players/PlayerList";
    private const string Component = "net/minecraft/network/chat/Component";
    private const string PlayerInfo = "net/minecraft/client/multiplayer/PlayerInfo";
    private const string PlayerSkin = "net/minecraft/client/resources/PlayerSkin";
    private const string ShareToLanScreen = "net/minecraft/client/gui/screens/ShareToLanScreen";
    private const string IntegratedServer = "net/minecraft/client/server/IntegratedServer";
    private const string MinecraftClient = "net/minecraft/client/Minecraft";
    private const string Gui = "net/minecraft/client/gui/Gui";
    private const string ChatComponent = "net/minecraft/client/gui/components/ChatComponent";
    private const string HttpUtil = "net/minecraft/util/HttpUtil";
    private const string GameType = "net/minecraft/world/level/GameType";
    private const string PublishCommand = "net/minecraft/server/commands/PublishCommand";
    private const string WorldData = "net/minecraft/world/level/storage/WorldData";
    private const string ClientPacketListener = "net/minecraft/client/multiplayer/ClientPacketListener";
    private const string Screen = "net/minecraft/client/gui/screens/Screen";
    private const string ServerLevel = "net/minecraft/server/level/ServerLevel";
    private const string ServerChunkCache = "net/minecraft/server/level/ServerChunkCache";
    private const string ServerPlayer = "net/minecraft/server/level/ServerPlayer";
    private const string ChunkMap = "net/minecraft/server/level/ChunkMap";
    private const string TrackedEntity = "net/minecraft/server/level/ChunkMap$TrackedEntity";
    private const string ClientInformationPacket =
        "net/minecraft/network/protocol/game/ServerboundClientInformationPacket";
    private const string Entity = "net/minecraft/world/entity/Entity";
    private const string ChunkPos = "net/minecraft/world/level/ChunkPos";
    private const string PlayerChunkSender = "net/minecraft/server/network/PlayerChunkSender";
    private const string LevelChunk = "net/minecraft/world/level/chunk/LevelChunk";
    private const string ChunkAccess = "net/minecraft/world/level/chunk/ChunkAccess";
    private const string NetworkPacket = "net/minecraft/network/protocol/Packet";
    private const string ChunkRadiusPacket =
        "net/minecraft/network/protocol/game/ClientboundSetChunkCacheRadiusPacket";
    // The rule about which hosts a skin may come from moved once and was
    // renamed once across the eighteen published authlib releases, and these
    // are the only three forms it has ever taken: isWhitelistedDomain on the
    // session service through 2.1.28, isAllowedTextureDomain on the session
    // service from 2.3.31 to 3.16.29, and isAllowedTextureDomain on
    // TextureUrlChecker from 3.18.38 on. Both classes are named and both method
    // names offered; the transformer patches whichever pair actually exists and
    // leaves the other class alone. Nothing here comes from the runtime's
    // mappings, because com.mojang.authlib is never obfuscated - which is what
    // makes this the one part of the adapter that is the same on every loader
    // and every Minecraft version.
    private const string TextureUrlChecker = "com/mojang/authlib/yggdrasil/TextureUrlChecker";
    private const string YggdrasilSessionService =
        "com/mojang/authlib/yggdrasil/YggdrasilMinecraftSessionService";
    // Where every Minecraft agrees who a player is. Whatever builds an offline
    // profile - the login listener on the old versions, UUIDUtil on the new -
    // ends at new GameProfile(offlineUuid, name), so this is the one seam that
    // needs no mappings and works on every loader.
    private const string GameProfile = "com/mojang/authlib/GameProfile";
    private readonly AppPaths _paths;

    public IdentityAdapterMappingService(AppPaths paths)
    {
        _paths = paths;
    }

    /// <summary>
    /// What to patch in this runtime: the UUID hooks and the skin hooks where
    /// both can be had, and the skin hooks alone where they cannot.
    /// </summary>
    /// <remarks>
    /// The two were one thing until the packs made it plain they are not. The
    /// UUID hooks patch Minecraft's own classes, so they need the runtime's
    /// mappings and a Minecraft new enough to have the classes they name. The
    /// skin hooks patch com.mojang.authlib, which no loader obfuscates and no
    /// mappings describe. Refusing both because the first cannot be had was
    /// costing the second for no reason: All The Fabric 3 is named in
    /// intermediary rather than TSRG2, and is 1.18.2, and RPG Ars Nouveau has
    /// mappings but is 1.20.1 - neither has PlayerSkin, which arrived later.
    /// (Intermediary is read since the third spelling went in; before that All
    /// The Fabric 3 had no names this could read at all.)
    /// Neither could show a skin, and the skin never needed either.
    /// </remarks>
    public IdentityAdapterConfiguration Build(PreparedRuntime runtime, string gameDirectory)
    {
        var mojangMappingPath = FindMojangMappings(runtime.LibrariesRoot);
        var mappingPath = FindTsrg2Mappings(runtime.LibrariesRoot);
        var intermediaryPath = mappingPath is null ? FindIntermediaryMappings(runtime) : null;
        // A NeoForge that ships neither is not a runtime without an answer: it
        // loads the game under Mojang's own names, and Mojang's own file is
        // enough on its own. NeoForge stopped producing the merged mappings
        // inside the 21.10 line, so this is what a pack built next year will
        // land on. If the guess is wrong the required classes are not found in
        // any jar and the whole thing falls back to skins, as it does now.
        var officialOnly = mappingPath is null && intermediaryPath is null &&
            runtime.Descriptor.Loader.Type == PackLoaderKind.NeoForge &&
            mojangMappingPath is not null;
        if (mappingPath is null && intermediaryPath is null && !officialOnly)
        {
            return BuildSkinsOnly(runtime, gameDirectory, "the runtime ships no mappings this build can read");
        }

        RuntimeNames mappings;
        try
        {
            mappings = mappingPath is not null ? RuntimeNames.Read(mappingPath, mojangMappingPath)
                : intermediaryPath is not null
                    ? RuntimeNames.ReadIntermediary(intermediaryPath, mojangMappingPath, runtime.ClientJarPath)
                    : RuntimeNames.ReadOfficialOnly(mojangMappingPath!);
        }
        catch (InvalidDataException ex)
        {
            return BuildSkinsOnly(runtime, gameDirectory, ex.Message);
        }
        mappingPath ??= intermediaryPath ?? mojangMappingPath!;

        try
        {
            return BuildEverything(runtime, gameDirectory, mappingPath, mappings);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            // A Minecraft the UUID hooks do not fit is still a Minecraft whose
            // skins work, and may well be one whose world can still be opened
            // to the network in one press: the classes that hook reaches for
            // are older than the ones that settle a UUID at the door. So the
            // reason is carried forward and every other feature asked for
            // again, one at a time.
            return BuildSkinsOnly(runtime, gameDirectory, ex.Message, mappings, mappingPath);
        }
    }

    /// <summary>
    /// The skin hooks, which need nothing from the runtime but its authlib.
    /// </summary>
    private IdentityAdapterConfiguration BuildSkinsOnly(
        PreparedRuntime runtime,
        string gameDirectory,
        string whyNotEverything,
        RuntimeNames? mappings = null,
        string? mappingPath = null)
    {
        var lanScreen = AddLanPublishProperties(mappings, out var lanProperties);
        var perPlayerProperties = new Dictionary<string, string>(StringComparer.Ordinal);
        var perPlayerChunks = AddPerPlayerChunksProperties(mappings, perPlayerProperties);
        AddChunkPacingProperties(mappings, perPlayerProperties, perPlayerChunks);
        var guestMovement = AddGuestMovementProperties(mappings, perPlayerProperties);
        var wanted = new HashSet<string>(StringComparer.Ordinal)
            { YggdrasilSessionService, TextureUrlChecker, GameProfile };
        if (lanScreen is not null)
        {
            wanted.Add(ShareToLanScreen);
            wanted.Add(lanScreen.ObfName);
        }

        AddPerPlayerChunkTargets(mappings, wanted, perPlayerChunks);
        AddGuestMovementTargets(mappings, wanted, guestMovement);
        var targets = FindRuntimeTargets(runtime, gameDirectory, wanted);
        // TextureUrlChecker is a class authlib only grew at 3.18.38, so its
        // absence is ordinary; the session service has been there throughout
        // and without it there is nothing to patch at all.
        var sessionService = targets.FirstOrDefault(target => target.ClassName == YggdrasilSessionService)
            ?? throw Unsupported(
                runtime.Descriptor,
                $"{whyNotEverything}, and authlib itself was not found either");
        if (targets.All(target => target.ClassName != GameProfile))
        {
            throw Unsupported(
                runtime.Descriptor,
                $"{whyNotEverything}, and authlib carries no GameProfile to key a player by");
        }

        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["identityHooksEnabled"] = "false",
            ["xaeroWaypointEnabled"] = "false",
            ["ftbTeleportEnabled"] = "false",
            ["solarFluxSyncEnabled"] = "false",
            // The preflight asks every alias list by name and refuses to run on
            // a missing one, so the switched-off hooks are still named. Their
            // names are what they always were - nothing matches them here
            // anyway, because identityHooksEnabled and the three Enabled flags
            // above are what decide whether anything is patched.
            ["loginClasses"] = LoginListener,
            ["playerInfoClasses"] = PlayerInfo,
            ["ftbTeleportClasses"] = JoinAliases(
                FtbWaypointRowPanel,
                FtbWaypointMapIcon,
                FtbTeleportFromMapPacket),
            ["solarFluxPackClasses"] = SolarFluxResourcePack,
            ["menuRegistrationFixEnabled"] = "true",
            ["menuRegistrationFixClasses"] = JoinAliases(Ae2AddonLibMenuRegistry, UfoModMenus),
            ["menuBuilderClasses"] = Ae2MenuTypeBuilder,
            ["menuBuilderRegisteringMethods"] = "build",
            ["menuBuilderUnregisteredMethods"] = "buildUnregistered",
            ["menuBuilderDescriptors"] = Ae2MenuBuildDescriptor,
            ["xaeroWaypointTeleportClasses"] = XaeroWaypointTeleport,
        };
        foreach (var pair in lanProperties) properties[pair.Key] = pair.Value;
        foreach (var pair in perPlayerProperties) properties[pair.Key] = pair.Value;
        // The screen has to be in a jar to be patched, and the preflight is
        // told to patch every target it is given. One that is not there means
        // the hook is off rather than half on.
        if (lanScreen is not null &&
            targets.All(target => target.ClassName != ShareToLanScreen && target.ClassName != lanScreen.ObfName))
        {
            properties["lanPublishEnabled"] = "false";
        }
        AddServeDistanceProperties(mappings, properties);
        AddSkinProperties(properties);
        // There is no mapping file behind this configuration, and the cache
        // above still wants a file to notice changing. authlib is the file it
        // was actually derived from, so authlib is what it watches - unless
        // there were mappings after all and only the UUID hooks did not fit,
        // in which case the file they came from is what changes.
        return new IdentityAdapterConfiguration(mappingPath ?? sessionService.JarPath, properties, targets);
    }

    /// <summary>The part that is the same however the rest turns out.</summary>
    private static void AddSkinProperties(Dictionary<string, string> properties)
    {
        properties["textureUrlCheckerClasses"] = JoinAliases(TextureUrlChecker, YggdrasilSessionService);
        properties["textureUrlCheckerMethods"] = JoinAliases("isAllowedTextureDomain", "isWhitelistedDomain");
        properties["textureUrlCheckerDescriptors"] = "(Ljava/lang/String;)Z";
        // Where the game asks authlib for a skin. getTextures took a profile up
        // to authlib 5.0.47, getPackedTextures takes one from 6.0.52 on, and
        // there has never been a third; the profile is given its skin there,
        // one step before authlib reads it.
        properties["skinReaderMethods"] = JoinAliases("getTextures", "getPackedTextures");
        properties["gameProfileClasses"] = GameProfile;
    }

    /// <summary>
    /// The one-press publish, and every name the hook reaches for once a world
    /// is open.
    ///
    /// Asked for separately because it is older than the hooks that settle a
    /// UUID at the door: those need startClientVerification, which is 1.20.2,
    /// while a world has been opened to the network the same way since 1.17. A
    /// Minecraft that has all of these gets the hook whether or not it has the
    /// others - and one missing any single name gets none of it, rather than a
    /// hook that half works.
    /// </summary>
    /// <returns>The screen to patch, or null where the hook cannot be had.</returns>
    private static MappedClass? AddLanPublishProperties(
        RuntimeNames? mappings,
        out Dictionary<string, string> properties)
    {
        // Named even when switched off: the preflight asks for this list by
        // name before it looks at anything else, and its absence is why a pack
        // with no mappings at all has been losing its skins too.
        properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lanPublishEnabled"] = "false",
            ["lanShareScreenClasses"] = ShareToLanScreen
        };
        if (mappings is null) return null;

        var shareScreen = mappings.FindClass(ShareToLanScreen);
        var integrated = mappings.FindClass(IntegratedServer);
        var minecraftClient = mappings.FindClass(MinecraftClient);
        var gui = mappings.FindClass(Gui);
        var chatComponent = mappings.FindClass(ChatComponent);
        var httpUtil = mappings.FindClass(HttpUtil);
        var gameType = mappings.FindClass(GameType);
        var publishCommand = mappings.FindClass(PublishCommand);
        var worldData = mappings.FindClass(WorldData);
        var screen = mappings.FindClass(Screen);
        var component = mappings.FindClass(Component);
        var server = mappings.FindClass(MinecraftServer);
        var playerList = mappings.FindClass(PlayerList);
        if (shareScreen is null || integrated is null || minecraftClient is null || gui is null ||
            chatComponent is null || httpUtil is null || gameType is null || publishCommand is null ||
            worldData is null || screen is null || component is null || server is null ||
            playerList is null)
        {
            return null;
        }

        var shareInit = shareScreen.FindMethod("init", descriptor => descriptor == "()V");
        var publishServer = integrated.FindMethod(
            "publishServer",
            descriptor => descriptor.EndsWith("ZI)Z", StringComparison.Ordinal));
        var defaultGameType = server.FindMethod(
            "getDefaultGameType",
            descriptor => descriptor.StartsWith("()L", StringComparison.Ordinal));
        var getWorldData = server.FindMethod(
            "getWorldData",
            descriptor => descriptor.StartsWith("()L", StringComparison.Ordinal));
        var isPublished = server.FindMethod("isPublished", descriptor => descriptor == "()Z");
        var getPlayerList = server.FindMethod(
            "getPlayerList",
            descriptor => descriptor.StartsWith("()L", StringComparison.Ordinal));
        // Renamed at 1.20.6 and not otherwise changed: getAllowCommands before
        // it, isAllowCommands after. Both are asked for rather than one being
        // guessed from the version, which would be a second thing to keep
        // right.
        var allowCommands = worldData.FindMethod("isAllowCommands", descriptor => descriptor == "()Z")
            ?? worldData.FindMethod("getAllowCommands", descriptor => descriptor == "()Z");
        var availablePort = httpUtil.FindMethod("getAvailablePort", descriptor => descriptor == "()I");
        var getInstance = minecraftClient.FindMethod(
            "getInstance",
            descriptor => descriptor.StartsWith("()L", StringComparison.Ordinal));
        var singleplayerServer = minecraftClient.FindMethod(
            "getSingleplayerServer",
            descriptor => descriptor.StartsWith("()L", StringComparison.Ordinal));
        var setScreen = minecraftClient.FindMethod(
            "setScreen",
            descriptor => descriptor == $"(L{screen.ObfName};)V");
        var updateTitle = minecraftClient.FindMethod("updateTitle", descriptor => descriptor == "()V");
        var guiField = minecraftClient.FindField("gui");
        var getChat = gui.FindMethod(
            "getChat",
            descriptor => descriptor.StartsWith("()L", StringComparison.Ordinal));
        var addMessage = chatComponent.FindMethod(
            "addMessage",
            descriptor => descriptor == $"(L{component.ObfName};)V");
        // The line the game writes in chat when a world opens is wanted, not
        // required: PublishCommand said it inline until 1.19.4, and a world
        // that opens without a line about it has still opened. The hook's own
        // failure here is caught and logged on the game's side.
        var publishSuccess = publishCommand.FindMethod(
            "getSuccessMessage",
            descriptor => descriptor.StartsWith("(I)L", StringComparison.Ordinal));
        if (shareInit is null || publishServer is null || defaultGameType is null ||
            getWorldData is null || isPublished is null || getPlayerList is null ||
            allowCommands is null || availablePort is null || getInstance is null ||
            singleplayerServer is null || setScreen is null || updateTitle is null ||
            guiField is null || getChat is null || addMessage is null)
        {
            return null;
        }

        properties["lanPublishEnabled"] = "true";
        properties["lanShareScreenClasses"] = JoinAliases(shareScreen);
        properties["lanShareInitMethods"] = JoinAliases(shareInit);
        properties["integratedServerClasses"] = JoinAliases(integrated);
        properties["publishServerMethods"] = JoinAliases(publishServer);
        properties["serverClasses"] = JoinAliases(server);
        properties["playerListClasses"] = JoinAliases(playerList);
        properties["playerListMethods"] = JoinAliases(getPlayerList);
        properties["getDefaultGameTypeMethods"] = JoinAliases(defaultGameType);
        properties["getWorldDataMethods"] = JoinAliases(getWorldData);
        properties["isPublishedMethods"] = JoinAliases(isPublished);
        properties["worldDataClasses"] = JoinAliases(worldData);
        properties["isAllowCommandsMethods"] = JoinAliases(allowCommands);
        properties["httpUtilClasses"] = JoinDottedAliases(httpUtil);
        properties["getAvailablePortMethods"] = JoinAliases(availablePort);
        properties["gameTypeClasses"] = JoinDottedAliases(gameType);
        properties["publishCommandClasses"] = JoinDottedAliases(publishCommand);
        if (publishSuccess is not null)
        {
            properties["publishSuccessMethods"] = JoinAliases(publishSuccess);
        }
        properties["minecraftClasses"] = JoinDottedAliases(minecraftClient);
        properties["minecraftGetInstanceMethods"] = JoinAliases(getInstance);
        properties["getSingleplayerServerMethods"] = JoinAliases(singleplayerServer);
        properties["setScreenMethods"] = JoinAliases(setScreen);
        properties["updateTitleMethods"] = JoinAliases(updateTitle);
        properties["minecraftGuiFields"] = JoinAliases(guiField);
        properties["screenClasses"] = JoinDottedAliases(screen);
        properties["componentClasses"] = JoinDottedAliases(component);
        properties["guiClasses"] = JoinAliases(gui);
        properties["guiChatMethods"] = JoinAliases(getChat);
        properties["chatComponentClasses"] = JoinAliases(chatComponent);
        properties["chatAddMessageMethods"] = JoinAliases(addMessage);
        return shareScreen;
    }

    /// <summary>
    /// Giving each guest the chunks he asked for on a Minecraft that does not
    /// know how.
    /// </summary>
    /// <remarks>
    /// From 1.20.2 the game does this itself: ChunkMap keeps a server-wide
    /// distance and hands each player min(his own, the server's), so raising
    /// the server's number is the whole feature. Before that there is one
    /// number for everybody - raising it raises it for every guest, including
    /// the one who set his slider to four and meant it.
    ///
    /// What is patched is the reading of that number, not the decisions made
    /// from it. ChunkMap.updatePlayerStatus and ChunkMap.move both take the
    /// player they are working on and then read this.viewDistance several
    /// times to work out which chunks he should have and which he had before;
    /// every one of those reads becomes "the smaller of the server's number and
    /// what this player asked for". The game's own arithmetic is left exactly
    /// as it is, which is the point: the two answers stay consistent with each
    /// other, so a chunk that is no longer his is unloaded through the same
    /// branch that unloads one he walked away from, and nothing is sent that
    /// was not asked for.
    ///
    /// The number each player asked for is not kept by the server on those
    /// versions, so it is taken as it arrives in ServerPlayer.updateOptions.
    ///
    /// Only ever installed where the game cannot do it: a version that has
    /// ServerPlayer.requestedViewDistance is a version that already does, and
    /// Mojang renamed the field to serverViewDistance in the same release.
    /// </remarks>
    private static bool AddPerPlayerChunksProperties(
        RuntimeNames? mappings,
        Dictionary<string, string> properties)
    {
        properties["perPlayerChunksEnabled"] = "false";
        // Named even when off: the preflight asks for these by name before it
        // looks at anything else.
        properties["chunkMapClasses"] = ChunkMap;
        properties["serverPlayerClasses"] = ServerPlayer;
        properties["trackedEntityClasses"] = TrackedEntity;
        if (mappings is null) return false;

        var serverPlayer = mappings.FindClass(ServerPlayer);
        if (serverPlayer is null) return false;

        // The game's own, and nothing to do. Asked before anything else is
        // looked up, so that a runtime which does this itself is turned away
        // for the reason it deserves: the settings packet moved to another
        // package in the very release that gave the server a number per
        // player, and from here one absence reads exactly like the other.
        if (serverPlayer.FindMethod("requestedViewDistance", descriptor => descriptor == "()I") is not null)
        {
            return false;
        }

        var chunkMap = mappings.FindClass(ChunkMap);
        var packet = mappings.FindClass(ClientInformationPacket);
        var entity = mappings.FindClass(Entity);
        var chunkPos = mappings.FindClass(ChunkPos);
        var levelChunk = mappings.FindClass(LevelChunk);
        var chunkAccess = mappings.FindClass(ChunkAccess);
        if (chunkMap is null || packet is null || entity is null ||
            chunkPos is null || levelChunk is null || chunkAccess is null)
        {
            return false;
        }

        var obfPlayer = $"(L{serverPlayer.ObfName};";
        var runtimePlayer = $"(L{serverPlayer.RuntimeName};";
        var updatePlayerStatus = chunkMap.FindMethod(
            "updatePlayerStatus",
            descriptor => descriptor == obfPlayer + "Z)V");
        var movePlayer = chunkMap.FindMethod("move", descriptor => descriptor == obfPlayer + ")V");
        var viewDistance = chunkMap.FindField("viewDistance");
        var updateOptions = serverPlayer.FindMethod(
            "updateOptions",
            descriptor => descriptor == $"(L{packet.ObfName};)V");
        // The packet only became a record at 1.18; before that the accessor is
        // spelled the old way and means the same thing.
        var clientViewDistance = packet.FindMethod("viewDistance", descriptor => descriptor == "()I")
            ?? packet.FindMethod("getViewDistance", descriptor => descriptor == "()I");
        var playerUuid = entity.FindMethod("getUUID", descriptor => descriptor == "()Ljava/util/UUID;");
        // The one path that does not go through either of those two: a chunk
        // that has finished loading is offered to everybody the server's radius
        // reaches, and the player it is being offered to is a loop variable
        // there rather than an argument, so the number cannot be narrowed where
        // it is read. It is narrowed at the delivery instead.
        var playerLoadedChunk = chunkMap.FindMethod(
            "playerLoadedChunk",
            descriptor => descriptor.StartsWith(obfPlayer, StringComparison.Ordinal) &&
                descriptor.EndsWith($"L{levelChunk.ObfName};)V", StringComparison.Ordinal));
        var chunkGetPos = chunkAccess.FindMethod(
            "getPos",
            descriptor => descriptor == $"()L{chunkPos.ObfName};");
        var chunkPosition = entity.FindMethod(
            "chunkPosition",
            descriptor => descriptor == $"()L{chunkPos.ObfName};");
        var chunkPosX = chunkPos.FindField("x");
        var chunkPosZ = chunkPos.FindField("z");
        // The way back from a player to the map that tracks him, so that a
        // player who changes his mind can be re-tracked at the new number
        // rather than waiting to walk into it.
        var serverLevel = mappings.FindClass(ServerLevel);
        var chunkCache = mappings.FindClass(ServerChunkCache);
        // Renamed at 1.20.1 and not otherwise changed: getLevel before it,
        // serverLevel after. On the older spelling there are two of them and
        // only one answers with a ServerLevel, which is what the descriptor
        // picks out.
        var playerLevel = serverLevel is null
            ? null
            : serverPlayer.FindMethod("serverLevel", descriptor => descriptor == $"()L{serverLevel.ObfName};")
                ?? serverPlayer.FindMethod("getLevel", descriptor => descriptor == $"()L{serverLevel.ObfName};");
        var chunkMapField = chunkCache?.FindField("chunkMap");
        // Entities are tracked by their own distance, capped by the server's -
        // "as far as this entity carries, but never past the world we send".
        // The cap is the same number, reached from an inner class through the
        // map's own instance, so it narrows the same way and a guest stops
        // being told about mobs standing where he has no ground.
        var trackedEntity = mappings.FindClass(TrackedEntity);
        var updatePlayer = trackedEntity?.FindMethod(
            "updatePlayer",
            descriptor => descriptor == obfPlayer + ")V");
        if (updatePlayerStatus is null || movePlayer is null || viewDistance is null ||
            updateOptions is null || clientViewDistance is null || playerUuid is null ||
            playerLoadedChunk is null || chunkGetPos is null || chunkPosition is null ||
            chunkPosX is null || chunkPosZ is null ||
            playerLevel is null || chunkMapField is null ||
            trackedEntity is null || updatePlayer is null)
        {
            return false;
        }

        properties["perPlayerChunksEnabled"] = "true";
        properties["chunkMapClasses"] = JoinAliases(chunkMap);
        properties["serverPlayerClasses"] = JoinAliases(serverPlayer);
        properties["updatePlayerStatusMethods"] = JoinAliases(updatePlayerStatus);
        // A name alone does not say which method: an obfuscated ServerPlayer
        // has twenty-two one-argument void methods called "a". The descriptor
        // is what tells them apart, in the same two spellings as everything
        // else - what this runtime loads, and what the vanilla jar holds.
        properties["updatePlayerStatusDescriptors"] =
            JoinAliases(runtimePlayer + "Z)V", obfPlayer + "Z)V");
        properties["movePlayerMethods"] = JoinAliases(movePlayer);
        properties["movePlayerDescriptors"] = JoinAliases(runtimePlayer + ")V", obfPlayer + ")V");
        properties["chunkViewDistanceFields"] = JoinAliases(viewDistance);
        properties["updateOptionsMethods"] = JoinAliases(updateOptions);
        properties["updateOptionsDescriptors"] = JoinAliases(
            $"(L{packet.RuntimeName};)V",
            $"(L{packet.ObfName};)V");
        properties["clientViewDistanceMethods"] = JoinAliases(clientViewDistance);
        properties["playerUuidMethods"] = JoinAliases(playerUuid);
        properties["levelChunkClasses"] = JoinAliases(levelChunk);
        properties["playerLoadedChunkMethods"] = JoinAliases(playerLoadedChunk);
        properties["chunkGetPosMethods"] = JoinAliases(chunkGetPos);
        properties["chunkPositionMethods"] = JoinAliases(chunkPosition);
        properties["chunkPosXFields"] = JoinAliases(chunkPosX);
        properties["chunkPosZFields"] = JoinAliases(chunkPosZ);
        properties["serverLevelMethods"] = JoinAliases(playerLevel);
        properties["chunkMapFields"] = JoinAliases(chunkMapField);
        properties["trackedEntityClasses"] = JoinAliases(trackedEntity);
        properties["updatePlayerMethods"] = JoinAliases(updatePlayer);
        properties["updatePlayerDescriptors"] = JoinAliases(runtimePlayer + ")V", obfPlayer + ")V");
        return true;
    }

    /// <summary>
    /// Letting a guest keep the ground he says he is standing on.
    /// </summary>
    /// <remarks>
    /// Minecraft measures every movement packet against how far a player could
    /// have travelled since the last one and puts him back where he was if the
    /// step is too long. On a public server that is what stops a flying cheat.
    /// On a world two friends opened to each other it stops nothing and costs
    /// something: half a second of packets not arriving over a Steam relay is
    /// enough to make the next one look like a leap, and the guest is thrown
    /// backwards for his own connection. Measured on All The Fabric 3 with
    /// chunk pacing already in and its queue never once backed up - the ground
    /// was arriving fine and he was still being pulled back twice in four
    /// minutes.
    ///
    /// The game already exempts one player from the test: whoever opened the
    /// world. The exemption is there for this exact reason and stops at the
    /// host only because vanilla knows nothing of a world opened to a friend
    /// rather than to the internet. So it is widened to everybody in one.
    ///
    /// Two methods, named. The same question decides who may change the
    /// world's difficulty and what happens when a connection ends, and neither
    /// is a guest's to answer - which is why nothing here touches the method
    /// that answers it.
    /// </remarks>
    private static bool AddGuestMovementProperties(
        RuntimeNames? mappings,
        Dictionary<string, string> properties)
    {
        // Every list, before anything can return: the preflight asks for them
        // by name and throws on one it was not given, taking the whole adapter
        // with it - skins included.
        properties["guestMovementEnabled"] = "false";
        properties["movementListenerClasses"] = GameListener;
        properties["handleMovePlayerMethods"] = "handleMovePlayer";
        properties["handleMovePlayerDescriptors"] = $"(L{MovePlayerPacket};)V";
        properties["handleMoveVehicleMethods"] = "handleMoveVehicle";
        properties["handleMoveVehicleDescriptors"] = $"(L{MoveVehiclePacket};)V";
        properties["singleplayerOwnerMethods"] = "isSingleplayerOwner";
        properties["singleplayerOwnerClasses"] = GameListener;
        if (mappings is null) return false;

        var listener = mappings.FindClass(GameListener);
        var movePlayer = mappings.FindClass(MovePlayerPacket);
        var moveVehicle = mappings.FindClass(MoveVehiclePacket);
        if (listener is null || movePlayer is null || moveVehicle is null) return false;

        var handleMovePlayer = listener.FindMethod(
            "handleMovePlayer", descriptor => descriptor == $"(L{movePlayer.ObfName};)V");
        var handleMoveVehicle = listener.FindMethod(
            "handleMoveVehicle", descriptor => descriptor == $"(L{moveVehicle.ObfName};)V");
        // On the listener until 1.20.1 and on the shared parent from 1.20.2.
        // Both are asked, and whichever answers is also what names the owner
        // the call carries in the bytecode.
        var common = mappings.FindClass(CommonListener);
        var owner = listener.FindMethod("isSingleplayerOwner", descriptor => descriptor == "()Z")
            ?? common?.FindMethod("isSingleplayerOwner", descriptor => descriptor == "()Z");
        if (handleMovePlayer is null || handleMoveVehicle is null || owner is null) return false;

        properties["guestMovementEnabled"] = "true";
        properties["movementListenerClasses"] = JoinAliases(listener);
        properties["handleMovePlayerMethods"] = JoinAliases(handleMovePlayer);
        properties["handleMovePlayerDescriptors"] = JoinAliases(
            $"(L{movePlayer.RuntimeName};)V", $"(L{movePlayer.ObfName};)V");
        properties["handleMoveVehicleMethods"] = JoinAliases(handleMoveVehicle);
        properties["handleMoveVehicleDescriptors"] = JoinAliases(
            $"(L{moveVehicle.RuntimeName};)V", $"(L{moveVehicle.ObfName};)V");
        properties["singleplayerOwnerMethods"] = JoinAliases(owner);
        // Both, and not only the class that declares it. A call to an
        // inherited method carries the name of the class it was called on, so
        // on 1.20.2 and later the instruction inside the listener says the
        // listener even though the method lives on the parent.
        properties["singleplayerOwnerClasses"] = common is null
            ? JoinAliases(listener)
            : JoinAliases(JoinAliases(listener), JoinAliases(common));
        return true;
    }

    /// <summary>
    /// The listener, named only where both its movement handlers and the
    /// question inside them were found.
    /// </summary>
    /// <remarks>
    /// A target the transformer will not change fails the preflight on
    /// byte equality and takes the whole adapter down with it, so this names
    /// nothing on a runtime where the feature switched itself off.
    /// </remarks>
    private static void AddGuestMovementTargets(
        RuntimeNames? mappings,
        HashSet<string> targets,
        bool guestMovement)
    {
        if (!guestMovement) return;
        var listener = mappings?.FindClass(GameListener);
        if (listener is null) return;
        targets.Add(listener.RuntimeName);
        targets.Add(listener.ObfName);
    }

    /// <summary>
    /// Handing a guest his chunks a few at a time instead of all at once.
    /// </summary>
    /// <remarks>
    /// From 1.20.2 the game paces this itself: PlayerChunkSender measures how
    /// quickly a client acknowledges a batch and sends the next one at that
    /// rate. Before that the server writes chunks to the connection as fast as
    /// it can, and everything else on that connection waits behind the flood -
    /// the guest's own actions on the way in, the server's answers on the way
    /// out. It is why eating plays but never finishes, why a blow lands late,
    /// and why a guest is thrown back by "moved too quickly": his client stalls
    /// on a burst of chunks and then reports one long step.
    ///
    /// The real mechanism cannot be brought back, because it needs the client
    /// to acknowledge what it received and no client before 1.20.2 knows those
    /// packets. The server's half can be: hold what will not fit in this tick
    /// and hand it over on the next, a few at a time.
    ///
    /// It rides on the per-player patch, and it gates itself off by the name of
    /// the class that does the pacing rather than by a version number. Where it
    /// cannot go in, the per-player narrowing is left exactly as it is - which
    /// is why this has a flag of its own instead of being folded into that one.
    /// </remarks>
    private static bool AddChunkPacingProperties(
        RuntimeNames? mappings,
        Dictionary<string, string> properties,
        bool perPlayerChunks)
    {
        // Written before anything here can return. The preflight asks for every
        // one of these by name, and one it cannot find takes down the whole
        // adapter, skins and all.
        properties["chunkPacingEnabled"] = "false";
        properties["chunkMapTickMethods"] = "tick";
        properties["chunkMapTickDescriptors"] = "()V";
        properties["untrackChunkMethods"] = "untrackChunk";
        properties["untrackChunkDescriptors"] = $"(L{ChunkPos};)V";
        properties["playerLatencyFields"] = "latency";
        if (!perPlayerChunks || mappings is null) return false;

        // The game's own, and nothing to do. Named by the class that does the
        // pacing rather than by the release: ServerPlayer.untrackChunk moved
        // into it in the same version, so either would answer today, and a
        // future release that separates the two lands this on the side that is
        // actually about pacing.
        if (mappings.FindClass(PlayerChunkSender) is not null) return false;

        var chunkMap = mappings.FindClass(ChunkMap);
        var serverPlayer = mappings.FindClass(ServerPlayer);
        var chunkPos = mappings.FindClass(ChunkPos);
        if (chunkMap is null || serverPlayer is null || chunkPos is null) return false;

        // The pump, once a tick. ChunkMap has other methods that take nothing
        // and answer nothing, so the descriptor alone will not do and the name
        // has to come with it.
        var tick = chunkMap.FindMethod("tick", descriptor => descriptor == "()V");
        // And the moment the server tells a client to forget a chunk, which is
        // the only thing that can take a held one back. Without it a chunk the
        // server has stopped believing in is handed over anyway and stays on
        // the client until he leaves the world.
        var untrackChunk = serverPlayer.FindMethod(
            "untrackChunk",
            descriptor => descriptor == $"(L{chunkPos.ObfName};)V");
        if (tick is null || untrackChunk is null) return false;

        properties["chunkPacingEnabled"] = "true";
        properties["chunkMapTickMethods"] = JoinAliases(tick);
        properties["untrackChunkMethods"] = JoinAliases(untrackChunk);
        properties["untrackChunkDescriptors"] = JoinAliases(
            $"(L{chunkPos.RuntimeName};)V",
            $"(L{chunkPos.ObfName};)V");
        // What the server already knows about the round trip to each player:
        // it answers a keep-alive every fifteen seconds and keeps the time it
        // took. Absent on 1.20.2 and later, where it moved onto the shared
        // listener - which is not a version this feature runs on anyway.
        var latency = serverPlayer.FindField("latency");
        if (latency is not null) properties["playerLatencyFields"] = JoinAliases(latency);
        return true;
    }

    /// <summary>
    /// The one method that says how far the server is about to serve.
    /// </summary>
    /// <remarks>
    /// Two names for one thing: setViewDistance until 1.20.1, and
    /// setServerViewDistance from 1.20.2, when the number stopped being the
    /// only one there was. Both take an int and answer nothing, and on every
    /// version in range it is the only method on the class that does.
    ///
    /// It is worth watching wherever a world is shared, and not only where the
    /// per-player patch goes in. The launcher writes its own number straight
    /// into the chunk map, on purpose, so that the integrated server's per-tick
    /// comparison against PlayerList never notices and never fights it - but
    /// that also means nothing tells the launcher when the host moves his own
    /// slider and narrows the world underneath a guest. Watching the setter is
    /// what turns that from a five second wait into one tick, and a version
    /// where the game hands out distances by itself needs it just as much.
    /// </remarks>
    private static MappedMember? ChunkMapSetter(RuntimeNames? mappings)
    {
        var chunkMap = mappings?.FindClass(ChunkMap);
        if (chunkMap is null) return null;
        return chunkMap.FindMethod("setViewDistance", descriptor => descriptor == "(I)V")
            ?? chunkMap.FindMethod("setServerViewDistance", descriptor => descriptor == "(I)V");
    }

    /// <summary>
    /// The classes the per-player chunk hook rewrites, under both the name this
    /// runtime loads and the obfuscated one.
    /// </summary>
    /// <remarks>
    /// Where the game hands out distances by itself there is nothing to rewrite
    /// in ServerPlayer or in the entity tracker, and naming a class the patch
    /// will not touch is not free: the preflight patches every target it is
    /// given and refuses one that came back unchanged, which is how a whole
    /// adapter was lost once already. So on those versions only the chunk map
    /// is named, and only for the setter.
    /// </remarks>
    private static void AddPerPlayerChunkTargets(
        RuntimeNames? mappings,
        HashSet<string> targets,
        bool perPlayerChunks)
    {
        var officials = perPlayerChunks
            ? new[] { ChunkMap, ServerPlayer, TrackedEntity }
            : ChunkMapSetter(mappings) is null ? [] : new[] { ChunkMap };
        foreach (var official in officials)
        {
            var mapped = mappings?.FindClass(official);
            if (mapped is null) continue;
            targets.Add(mapped.RuntimeName);
            targets.Add(mapped.ObfName);
        }
    }

    /// <summary>
    /// Serving a world further than the host draws it, which needs names an
    /// older Minecraft may not have under these spellings - ServerPlayer only
    /// began to remember what a client asked for in 1.20.2. Where any is
    /// missing the feature is absent and everything else is untouched: a
    /// version that cannot serve further must not lose its skins over it.
    /// </summary>
    private static void AddServeDistanceProperties(
        RuntimeNames? mappings,
        Dictionary<string, string> properties)
    {
        if (mappings is null) return;
        var serverLevel = mappings.FindClass(ServerLevel);
        var chunkCache = mappings.FindClass(ServerChunkCache);
        var serverPlayer = mappings.FindClass(ServerPlayer);
        var server = mappings.FindClass(MinecraftServer);
        var playerList = mappings.FindClass(PlayerList);
        if (serverLevel is null || chunkCache is null || serverPlayer is null ||
            server is null || playerList is null)
        {
            return;
        }

        // The integrated server copies the host's render distance into
        // PlayerList.setViewDistance every tick, and that number is then the
        // ceiling for everybody; ChunkMap keeps its own copy, and writing to
        // that one instead leaves the comparison the server makes each tick
        // still true, so it never writes over it.
        var allLevels = server.FindMethod(
            "getAllLevels",
            descriptor => descriptor == "()Ljava/lang/Iterable;");
        var chunkSource = serverLevel.FindMethod(
            "getChunkSource",
            descriptor => descriptor == $"()L{chunkCache.ObfName};");
        var chunkViewDistance = chunkCache.FindMethod(
            "setViewDistance",
            descriptor => descriptor == "(I)V");
        var players = playerList.FindMethod(
            "getPlayers",
            descriptor => descriptor == "()Ljava/util/List;");
        var requestedViewDistance = serverPlayer.FindMethod(
            "requestedViewDistance",
            descriptor => descriptor == "()I");
        // And the telling of it. The server sends a client only what that
        // client was told to expect: ClientChunkCache drops a chunk outside the
        // radius it last heard, with "Ignoring chunk since it's not in the view
        // range" in the log and nothing on the screen. The number is announced
        // by PlayerList.setViewDistance, which this deliberately does not call
        // - calling it would be undone by the next tick - so the announcement
        // has to be made here instead.
        var networkPacket = mappings.FindClass(NetworkPacket);
        var radiusPacket = mappings.FindClass(ChunkRadiusPacket);
        var broadcastAll = networkPacket is null
            ? null
            : playerList.FindMethod(
                "broadcastAll",
                descriptor => descriptor == $"(L{networkPacket.ObfName};)V");
        if (allLevels is null || chunkSource is null || chunkViewDistance is null || players is null ||
            networkPacket is null || radiusPacket is null || broadcastAll is null)
        {
            return;
        }

        // What each player asked for. From 1.20.2 the server remembers it; on
        // everything older the adapter has to hear it said and write it down,
        // and if it cannot do that either then serving further would serve
        // further to everybody, which is not what was asked for.
        if (requestedViewDistance is null && properties.GetValueOrDefault("perPlayerChunksEnabled") != "true")
        {
            return;
        }

        properties["serverLevelClasses"] = JoinAliases(serverLevel);
        properties["chunkSourceClasses"] = JoinAliases(chunkCache);
        var chunkMapClass = mappings.FindClass(ChunkMap);
        var chunkMapSetter = ChunkMapSetter(mappings);
        if (chunkMapClass is not null && chunkMapSetter is not null)
        {
            properties["chunkMapClasses"] = JoinAliases(chunkMapClass);
            properties["chunkSetViewDistanceMethods"] = JoinAliases(chunkMapSetter);
            properties["chunkSetViewDistanceDescriptors"] = "(I)V";
        }
        properties["getAllLevelsMethods"] = JoinAliases(allLevels);
        properties["getChunkSourceMethods"] = JoinAliases(chunkSource);
        properties["setChunkViewDistanceMethods"] = JoinAliases(chunkViewDistance);
        properties["getPlayersMethods"] = JoinAliases(players);
        properties["networkPacketClasses"] = JoinDottedAliases(networkPacket);
        properties["chunkRadiusPacketClasses"] = JoinDottedAliases(radiusPacket);
        properties["broadcastAllMethods"] = JoinAliases(broadcastAll);
        if (requestedViewDistance is not null)
        {
            properties["requestedViewDistanceMethods"] = JoinAliases(requestedViewDistance);
        }
    }

    private IdentityAdapterConfiguration BuildEverything(
        PreparedRuntime runtime,
        string gameDirectory,
        string mappingPath,
        RuntimeNames mappings)
    {
        var listener = mappings.RequireClass(LoginListener);
        var packet = mappings.RequireClass(HelloPacket);
        var server = mappings.RequireClass(MinecraftServer);
        var connection = mappings.RequireClass(Connection);
        var playerList = mappings.RequireClass(PlayerList);
        var component = mappings.RequireClass(Component);
        var playerInfo = mappings.RequireClass(PlayerInfo);
        var playerSkin = mappings.RequireClass(PlayerSkin);
        var clientPacketListener = mappings.RequireClass(ClientPacketListener);
        var screen = mappings.RequireClass(Screen);

        var hello = listener.RequireMethod("handleHello", descriptor => descriptor.Contains($"L{packet.ObfName};", StringComparison.Ordinal));
        var verify = listener.RequireMethod("verifyLoginAndFinishConnectionSetup", descriptor => descriptor.Contains("Lcom/mojang/authlib/GameProfile;", StringComparison.Ordinal));
        var skinLookup = playerInfo.RequireMethod(
            "createSkinLookup",
            descriptor => descriptor.StartsWith("(Lcom/mojang/authlib/GameProfile;)", StringComparison.Ordinal));
        var skinSelection = playerInfo.RequireMethod(
            "lambda$createSkinLookup$2",
            descriptor => descriptor.StartsWith("(Ljava/util/concurrent/CompletableFuture;", StringComparison.Ordinal));
        var sendUnsignedCommand = clientPacketListener.RequireMethod(
            "sendUnsignedCommand",
            descriptor => descriptor == "(Ljava/lang/String;)Z");
        var sendCommand = clientPacketListener.RequireMethod(
            "sendCommand",
            descriptor => descriptor == "(Ljava/lang/String;)V");
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["loginClasses"] = JoinAliases(listener),
            ["packetClasses"] = JoinAliases(packet),
            ["serverClasses"] = JoinAliases(server),
            ["connectionClasses"] = JoinAliases(connection),
            ["playerListClasses"] = JoinAliases(playerList),
            ["helloMethods"] = JoinAliases(hello),
            ["helloDescriptors"] = JoinAliases(
                "(Lnet/minecraft/network/protocol/login/ServerboundHelloPacket;)V",
                hello.ObfDescriptor),
            ["verifyMethods"] = JoinAliases(verify),
            ["verifyDescriptors"] = JoinAliases("(Lcom/mojang/authlib/GameProfile;)V", verify.ObfDescriptor),
            ["serverFields"] = JoinAliases(listener.RequireField("server")),
            ["connectionFields"] = JoinAliases(listener.RequireField("connection")),
            ["requestedUsernameFields"] = JoinAliases(listener.RequireField("requestedUsername")),
            ["packetNameMethods"] = JoinAliases(packet.RequireMethod("name", descriptor => descriptor == "()Ljava/lang/String;")),
            ["packetUuidMethods"] = JoinAliases(packet.RequireMethod("profileId", descriptor => descriptor == "()Ljava/util/UUID;")),
            ["memoryConnectionMethods"] = JoinAliases(connection.RequireMethod("isMemoryConnection", descriptor => descriptor == "()Z")),
            ["startVerificationMethods"] = JoinAliases(listener.RequireMethod("startClientVerification", descriptor => descriptor.Contains("Lcom/mojang/authlib/GameProfile;", StringComparison.Ordinal))),
            ["playerListMethods"] = JoinAliases(server.RequireMethod("getPlayerList", descriptor => descriptor.StartsWith("()L", StringComparison.Ordinal))),
            ["getPlayerMethods"] = JoinAliases(playerList.RequireMethod("getPlayer", descriptor => descriptor.StartsWith("(Ljava/util/UUID;)", StringComparison.Ordinal))),
            ["componentClasses"] = JoinDottedAliases(component),
            ["componentLiteralMethods"] = JoinAliases(component.RequireMethod("literal", descriptor => descriptor.StartsWith("(Ljava/lang/String;)", StringComparison.Ordinal))),
            ["disconnectMethods"] = JoinAliases(listener.RequireMethod("disconnect", descriptor => descriptor.EndsWith(")V", StringComparison.Ordinal))),
            ["playerInfoClasses"] = JoinAliases(playerInfo),
            ["skinLookupMethods"] = JoinAliases(skinLookup),
            ["skinLookupDescriptors"] = JoinAliases(
                "(Lcom/mojang/authlib/GameProfile;)Ljava/util/function/Supplier;",
                skinLookup.ObfDescriptor),
            ["skinSelectionMethods"] = JoinAliases(skinSelection),
            ["skinSelectionDescriptors"] = JoinAliases(
                "(Ljava/util/concurrent/CompletableFuture;Lnet/minecraft/client/resources/PlayerSkin;Z)Lnet/minecraft/client/resources/PlayerSkin;",
                skinSelection.ObfDescriptor),
            ["skinTextureUrlMethods"] = JoinAliases(playerSkin.RequireMethod("textureUrl", descriptor => descriptor == "()Ljava/lang/String;")),
            ["skinSecureMethods"] = JoinAliases(playerSkin.RequireMethod("secure", descriptor => descriptor == "()Z")),
            // The agent reads one fixed property set, so the transformers the
            // launcher no longer drives keep their keys and stay switched off.
            ["xaeroWaypointEnabled"] = "false",
            ["ftbTeleportEnabled"] = "false",
            ["ftbTeleportClasses"] = JoinAliases(
                FtbWaypointRowPanel,
                FtbWaypointMapIcon,
                FtbTeleportFromMapPacket),
            ["ftbPermissionMethods"] = "hasPermissions",
            ["solarFluxSyncEnabled"] = "false",
            ["solarFluxPackClasses"] = SolarFluxResourcePack,
            ["solarFluxSyncMethods"] = "init,listResources,getNamespaces,getResource",
            ["menuRegistrationFixEnabled"] = "true",
            ["menuRegistrationFixClasses"] = JoinAliases(Ae2AddonLibMenuRegistry, UfoModMenus),
            ["menuBuilderClasses"] = Ae2MenuTypeBuilder,
            ["menuBuilderRegisteringMethods"] = "build",
            ["menuBuilderUnregisteredMethods"] = "buildUnregistered",
            ["menuBuilderDescriptors"] = Ae2MenuBuildDescriptor,
            ["xaeroWaypointTeleportClasses"] = XaeroWaypointTeleport,
            ["xaeroWaypointTeleportMethods"] = "teleportToWaypoint",
            ["xaeroWaypointTeleportDescriptors"] = JoinAliases(
                "(Lxaero/common/minimap/waypoints/Waypoint;Lxaero/hud/minimap/world/MinimapWorld;" +
                "Lnet/minecraft/client/gui/screens/Screen;Z)V",
                "(Lxaero/common/minimap/waypoints/Waypoint;Lxaero/hud/minimap/world/MinimapWorld;" +
                $"L{screen.ObfName};Z)V"),
            ["clientPacketListenerClasses"] = JoinAliases(clientPacketListener),
            ["sendUnsignedCommandMethods"] = JoinAliases(sendUnsignedCommand),
            ["sendCommandMethods"] = JoinAliases(sendCommand),
            ["screenClasses"] = JoinDottedAliases(screen)
        };
        // The one-press publish and the serve distance are asked for
        // separately, because they are older than the hooks above and a
        // Minecraft that cannot have those may still have these.
        var lanScreen = AddLanPublishProperties(mappings, out var lanProperties);
        foreach (var pair in lanProperties) properties[pair.Key] = pair.Value;
        var perPlayerChunks = AddPerPlayerChunksProperties(mappings, properties);
        AddChunkPacingProperties(mappings, properties, perPlayerChunks);
        var guestMovement = AddGuestMovementProperties(mappings, properties);
        AddServeDistanceProperties(mappings, properties);

        AddSkinProperties(properties);

        var requiredTargets = new HashSet<string>(StringComparer.Ordinal)
        {
            LoginListener,
            listener.ObfName,
            PlayerInfo,
            playerInfo.ObfName,
            YggdrasilSessionService,
            GameProfile
        };
        if (lanScreen is not null)
        {
            requiredTargets.Add(ShareToLanScreen);
            requiredTargets.Add(lanScreen.ObfName);
        }
        AddPerPlayerChunkTargets(mappings, requiredTargets, perPlayerChunks);
        AddGuestMovementTargets(mappings, requiredTargets, guestMovement);
        // Wanted where it exists, not required: authlib only grew
        // TextureUrlChecker at 3.18.38, and every version before that keeps the
        // same rule on the session service instead.
        var wanted = new HashSet<string>(requiredTargets, StringComparer.Ordinal) { TextureUrlChecker };
        var targets = FindRuntimeTargets(runtime, gameDirectory, wanted);
        var found = targets.Select(target => target.ClassName).ToHashSet(StringComparer.Ordinal);
        if (!requiredTargets.IsSubsetOf(found))
        {
            var missing = string.Join(", ", requiredTargets.Where(target => !found.Contains(target)));
            throw Unsupported(runtime.Descriptor, $"required runtime classes are absent: {missing}");
        }

        return new IdentityAdapterConfiguration(mappingPath, properties, targets);
    }

    /// <summary>
    /// Mojang's own mappings for this version, if the runtime carries them.
    ///
    /// Forge's installer downloads them beside the client jar, and they are
    /// what turns an SRG number back into a name anybody can ask for. NeoForge
    /// downloads them too and has no use for them here, because its own merged
    /// file already answers in official names.
    /// </summary>
    private static string? FindMojangMappings(string librariesRoot)
    {
        var libraries = Path.Combine(librariesRoot, "net", "minecraft", "client");
        if (!Directory.Exists(libraries)) return null;
        foreach (var path in Directory.EnumerateFiles(libraries, "*mappings*.txt", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var reader = new StreamReader(path);
                // The first line of Mojang's file is their copyright notice,
                // and the first line that is not is a class: "a.b.C -> d:".
                for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (char.IsWhiteSpace(line[0])) break;
                    if (line.EndsWith(':') && line.Contains(" -> ", StringComparison.Ordinal)) return path;
                    break;
                }
            }
            catch (IOException)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// Fabric's and Quilt's mappings, which are one jar with one file in it.
    ///
    /// Both loaders put the same artefact on the launch classpath -
    /// net.fabricmc:intermediary - and both remap the game to that namespace,
    /// so there is no loader-specific branch here. What is in it is obfuscated
    /// to intermediary and nothing else: it says a class is class_3218 and
    /// never that it is ServerLevel, which is why Mojang's own file has to be
    /// beside it.
    /// </summary>
    private static string? FindIntermediaryMappings(PreparedRuntime runtime)
    {
        var libraries = Path.Combine(runtime.LibrariesRoot, "net", "fabricmc", "intermediary");
        if (!Directory.Exists(libraries)) return null;
        var wanted = $"intermediary-{runtime.Descriptor.MinecraftVersion}.jar";
        return Directory.EnumerateFiles(libraries, "*.jar", SearchOption.AllDirectories)
            .OrderByDescending(path => string.Equals(
                Path.GetFileName(path), wanted, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private static string? FindTsrg2Mappings(string librariesRoot)
    {
        var libraries = librariesRoot;
        if (!Directory.Exists(libraries)) return null;
        foreach (var path in Directory.EnumerateFiles(libraries, "*mappings*.txt", SearchOption.AllDirectories)
                     .OrderByDescending(path => Path.GetFileName(path).Contains("merged", StringComparison.OrdinalIgnoreCase))
                     .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var reader = new StreamReader(path);
                if (reader.ReadLine()?.StartsWith("tsrg2 ", StringComparison.Ordinal) == true) return path;
            }
            catch (IOException)
            {
            }
        }
        return null;
    }

    private List<IdentityAdapterTarget> FindRuntimeTargets(
        PreparedRuntime runtime,
        string gameDirectory,
        IReadOnlySet<string> requiredTargets)
    {
        var wanted = new HashSet<string>(requiredTargets, StringComparer.Ordinal);
        var candidates = new List<string>();
        var minecraftLibraries = Path.Combine(runtime.LibrariesRoot, "net", "minecraft", "client");
        if (Directory.Exists(minecraftLibraries))
        {
            candidates.AddRange(Directory.EnumerateFiles(minecraftLibraries, "*-srg.jar", SearchOption.AllDirectories));
        }
        candidates.Add(runtime.ClientJarPath);
        var normalizedGameDirectory = Path.GetFullPath(gameDirectory);
        _paths.EnsureUnderRoot(normalizedGameDirectory);
        var instanceMods = Path.Combine(normalizedGameDirectory, "mods");
        if (Directory.Exists(instanceMods))
        {
            candidates.AddRange(Directory.EnumerateFiles(instanceMods, "*.jar", SearchOption.TopDirectoryOnly));
        }
        var libraries = runtime.LibrariesRoot;
        if (Directory.Exists(libraries))
        {
            candidates.AddRange(Directory.EnumerateFiles(libraries, "*.jar", SearchOption.AllDirectories));
        }

        var found = new List<IdentityAdapterTarget>();
        var inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in candidates)
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) || !inspected.Add(fullPath)) continue;
            try
            {
                using var archive = ZipFile.OpenRead(fullPath);
                foreach (var className in wanted.ToArray())
                {
                    if (archive.GetEntry(className + ".class") is null) continue;
                    found.Add(new IdentityAdapterTarget(fullPath, className));
                    wanted.Remove(className);
                }
            }
            catch (InvalidDataException)
            {
            }
            catch (IOException)
            {
            }
            if (wanted.Count == 0) break;
        }
        return found;
    }

    /// <summary>
    /// What goes on the command line for one name: the spelling this runtime
    /// loads it under, then the obfuscated one. Anything that turns out to be
    /// the same word twice collapses to one, and the preflight's positional
    /// read clamps to what is there.
    /// </summary>
    private static string JoinAliases(MappedMember member) =>
        JoinAliases(member.RuntimeName, member.ObfName);

    private static string JoinAliases(MappedClass mapped) =>
        JoinAliases(mapped.RuntimeName, mapped.ObfName);

    /// <summary>The same, for a name the hooks pass to Class.forName.</summary>
    private static string JoinDottedAliases(MappedClass mapped) =>
        JoinAliases(mapped.RuntimeName.Replace('/', '.'), mapped.ObfName.Replace('/', '.'));

    private static string JoinAliases(params string[] aliases) => string.Join(",", aliases
        .Where(alias => !string.IsNullOrWhiteSpace(alias))
        .Distinct(StringComparer.Ordinal));

    /// <summary>
    /// Said of a runtime the hooks do not fit, rather than of a fault: the
    /// caller starts the pack without them. Forge and NeoForge ship the TSRG2
    /// mappings this reads and Fabric and Quilt ship none, and no version of
    /// Minecraft is obliged to keep the classes the hooks reach for.
    /// </summary>
    private static NotSupportedException Unsupported(PackRuntimeDescriptor descriptor, string reason) => new(
        $"Minecraft {descriptor.MinecraftVersion} {descriptor.Loader.Type} {descriptor.Loader.Version}: {reason}.");

    /// <summary>
    /// One name, in the three spellings that matter.
    ///
    /// The launcher knows a class as Mojang named it. The game may load it
    /// under that same name - NeoForge does since 1.20.2, and so does every
    /// loader for a class Mojang never obfuscated - or under an SRG name, which
    /// is what Forge remaps to and looks like <c>m_12345_</c> and
    /// <c>f_12345_</c>, or under an intermediary one on Fabric and Quilt.
    /// Underneath all of them is the obfuscated name in the jar Mojang ships,
    /// which is what the vanilla client jar on disk still holds.
    ///
    /// Two of the three go on the command line, in this order: the name the
    /// runtime loads, then the obfuscated one. The hooks try each in turn, and
    /// the preflight reads them positionally - one flavour per index - so the
    /// order is not decoration. What the launcher itself calls the name never
    /// goes out at all; it is only how this file asks for it.
    /// </summary>
    private sealed record MappedMember(
        string Official,
        string ObfName,
        string RuntimeName,
        string ObfDescriptor);

    private sealed class MappedClass
    {
        public MappedClass(string official, string obfName, string runtimeName)
        {
            Official = official;
            ObfName = obfName;
            RuntimeName = runtimeName;
        }

        public string Official { get; }
        public string ObfName { get; }
        public string RuntimeName { get; }
        public Dictionary<string, MappedMember> Fields { get; } = new(StringComparer.Ordinal);
        public List<MappedMember> Methods { get; } = [];

        public MappedMember RequireField(string official) => Fields.TryGetValue(official, out var field)
            ? field
            : throw new InvalidDataException($"Required identity mapping field is missing: {Official}.{official}");

        public MappedMember? FindField(string official) =>
            Fields.TryGetValue(official, out var field) ? field : null;

        public MappedMember RequireMethod(string official, Func<string, bool> descriptorPredicate) =>
            FindMethod(official, descriptorPredicate)
            ?? throw new InvalidDataException($"Required identity mapping method is missing: {Official}.{official}");

        /// <summary>The same, for a method only one feature needs.</summary>
        public MappedMember? FindMethod(string official, Func<string, bool> descriptorPredicate) =>
            Methods.FirstOrDefault(method =>
                method.Official == official && descriptorPredicate(method.ObfDescriptor));
    }

    /// <summary>
    /// Every name this build reaches for, in the spelling this runtime loads.
    /// </summary>
    /// <remarks>
    /// Two shapes of file answer this, and which one is on disk says which
    /// loader prepared the runtime. NeoForge's installer merges Mojang's
    /// mappings into a TSRG2 whose right column is the official name, and since
    /// 1.20.2 that is also the name the game loads - so one file is the whole
    /// answer. Forge's mcp_config ships a TSRG2 whose right column is SRG
    /// instead, and SRG names nothing this launcher can ask for: the official
    /// name is not in that file at all. The bridge is Mojang's own proguard
    /// mappings, which every Forge runtime already carries beside the client
    /// jar - official to obfuscated there, obfuscated to SRG here.
    ///
    /// Only the classes named in <see cref="Required"/> are kept, out of some
    /// seven thousand: the rest is a megabyte and a half of parsing nobody
    /// needs.
    /// </remarks>
    private sealed class RuntimeNames
    {
        private readonly Dictionary<string, MappedClass> _classes;

        private RuntimeNames(Dictionary<string, MappedClass> classes)
        {
            _classes = classes;
        }

        /// <summary>The classes this build asks about, by their official names.</summary>
        private static HashSet<string> Required { get; } = new(StringComparer.Ordinal)
        {
            LoginListener,
            GameListener,
            CommonListener,
            MovePlayerPacket,
            MoveVehiclePacket,
            HelloPacket,
            MinecraftServer,
            Connection,
            PlayerList,
            Component,
            PlayerInfo,
            PlayerSkin,
            ClientPacketListener,
            Screen,
            ServerLevel,
            ServerChunkCache,
            ServerPlayer,
            ChunkMap,
            TrackedEntity,
            ClientInformationPacket,
            Entity,
            ChunkPos,
            PlayerChunkSender,
            LevelChunk,
            ChunkAccess,
            NetworkPacket,
            ChunkRadiusPacket,
            ShareToLanScreen,
            IntegratedServer,
            MinecraftClient,
            Gui,
            ChatComponent,
            HttpUtil,
            GameType,
            PublishCommand,
            WorldData
        };

        public static RuntimeNames Read(string tsrg2Path, string? proguardPath)
        {
            var rows = ReadTsrg2(tsrg2Path);
            return IsSrgFlavoured(rows) ? Compose(rows, proguardPath) : FromOfficialTsrg2(rows);
        }

        /// <summary>
        /// A TSRG2 whose members are numbered rather than named. Forge's
        /// mcp_config file says <c>m_8354_</c> where NeoForge's says
        /// <c>setViewDistance</c>, and no header distinguishes them - both open
        /// "tsrg2 left right" - so the contents decide.
        /// </summary>
        private static bool IsSrgFlavoured(List<Tsrg2Row> rows) => rows.Any(row =>
            row.Methods.Any(method => SrgNamePattern.IsMatch(method.RightName)) ||
            row.Fields.Any(field => SrgNamePattern.IsMatch(field.RightName)));

        /// <summary>What an SRG member is called: m_ or f_, a number, an underscore.</summary>
        private static readonly System.Text.RegularExpressions.Regex SrgNamePattern =
            new(@"^[mf]_\d+_$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The NeoForge shape: the right column is already what we ask for.</summary>
        private static RuntimeNames FromOfficialTsrg2(List<Tsrg2Row> rows)
        {
            var classes = new Dictionary<string, MappedClass>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var mapped = new MappedClass(row.RightName, row.LeftName, row.RightName);
                foreach (var field in row.Fields)
                {
                    mapped.Fields[field.RightName] = new MappedMember(
                        field.RightName, field.LeftName, field.RightName, string.Empty);
                }
                foreach (var method in row.Methods)
                {
                    mapped.Methods.Add(new MappedMember(
                        method.RightName, method.LeftName, method.RightName, method.ObfDescriptor));
                }
                classes[row.RightName] = mapped;
            }
            return new RuntimeNames(classes);
        }

        /// <summary>
        /// The Forge shape: obfuscated to SRG here, official to obfuscated in
        /// Mojang's own file, joined on the obfuscated name and descriptor.
        /// </summary>
        private static RuntimeNames Compose(List<Tsrg2Row> rows, string? proguardPath)
        {
            if (proguardPath is null)
            {
                throw new InvalidDataException(
                    "the runtime's mappings name members by number and Mojang's own mappings, " +
                    "which say what those numbers stand for, are not beside them");
            }

            var proguard = ReadProguard(proguardPath);
            var byObfClass = rows.ToDictionary(row => row.LeftName, StringComparer.Ordinal);
            var classes = new Dictionary<string, MappedClass>(StringComparer.Ordinal);
            foreach (var (official, entry) in proguard)
            {
                if (!byObfClass.TryGetValue(entry.ObfName, out var row)) continue;
                // The SRG class name and the official one have been the same
                // since 1.17, which is where Forge's own support starts; the
                // right column is used rather than assumed all the same.
                var mapped = new MappedClass(official, entry.ObfName, row.RightName);
                var fields = row.Fields.ToDictionary(field => field.LeftName, field => field.RightName, StringComparer.Ordinal);
                foreach (var (fieldName, obfField) in entry.Fields)
                {
                    if (!fields.TryGetValue(obfField, out var runtime)) continue;
                    mapped.Fields[fieldName] = new MappedMember(fieldName, obfField, runtime, string.Empty);
                }
                var methods = row.Methods.ToLookup(method => method.LeftName, StringComparer.Ordinal);
                foreach (var member in entry.Methods)
                {
                    var descriptor = ObfDescriptorOf(member, proguard);
                    var match = methods[member.ObfName].FirstOrDefault(method => method.ObfDescriptor == descriptor);
                    // A member proguard names and the SRG file does not is a
                    // bridge or a synthetic; the real one is in there under the
                    // same name and a different descriptor.
                    if (match is null) continue;
                    mapped.Methods.Add(new MappedMember(
                        member.Official, member.ObfName, match.RightName, descriptor));
                }
                classes[official.Replace('.', '/')] = mapped;
            }
            return new RuntimeNames(classes);
        }

        /// <summary>
        /// A member's descriptor written in obfuscated names, which is the only
        /// namespace both files share. Proguard writes Java source types -
        /// "net.minecraft.server.level.ServerChunkCache" and "int[]" - so every
        /// one of them goes back through the class map.
        /// </summary>
        private static string ObfDescriptorOf(
            ProguardMethod member,
            Dictionary<string, ProguardClass> proguard)
        {
            var descriptor = new System.Text.StringBuilder("(");
            foreach (var parameter in member.Parameters)
            {
                descriptor.Append(ObfTypeOf(parameter, proguard));
            }
            return descriptor.Append(')').Append(ObfTypeOf(member.ReturnType, proguard)).ToString();
        }

        private static string ObfTypeOf(string javaType, Dictionary<string, ProguardClass> proguard)
        {
            var arrays = 0;
            while (javaType.EndsWith("[]", StringComparison.Ordinal))
            {
                arrays++;
                javaType = javaType[..^2];
            }
            var core = javaType switch
            {
                "void" => "V",
                "boolean" => "Z",
                "byte" => "B",
                "char" => "C",
                "short" => "S",
                "int" => "I",
                "long" => "J",
                "float" => "F",
                "double" => "D",
                _ => "L" + (proguard.TryGetValue(javaType, out var known)
                    ? known.ObfName
                    : javaType.Replace('.', '/')) + ";"
            };
            return new string('[', arrays) + core;
        }

        /// <summary>
        /// The Fabric and Quilt shape: obfuscated to intermediary in the
        /// loader's own jar, official to obfuscated in Mojang's, joined on the
        /// obfuscated name and descriptor.
        /// </summary>
        /// <remarks>
        /// Two things here are not in the other readers. A class Mojang never
        /// obfuscated has no line at all in the intermediary file - the
        /// remapper leaves it alone - so its absence means the name is already
        /// right rather than missing. And intermediary names a method once, at
        /// the class that declares it: an override carries the same name and no
        /// line of its own, so a miss on the owner is answered by asking its
        /// superclass, which is read out of the jar Mojang ships.
        /// </remarks>
        public static RuntimeNames ReadIntermediary(
            string intermediaryJarPath,
            string? proguardPath,
            string clientJarPath)
        {
            if (proguardPath is null)
            {
                throw new InvalidDataException(
                    "the runtime names the game in intermediary and Mojang's own mappings, " +
                    "which say what those names stand for, are not beside them");
            }

            var tiny = ReadTinyV1(intermediaryJarPath);
            var proguard = ReadProguard(proguardPath);
            using var hierarchy = new JarHierarchy(clientJarPath);
            var classes = new Dictionary<string, MappedClass>(StringComparer.Ordinal);
            foreach (var (official, entry) in proguard)
            {
                var slashed = official.Replace('.', '/');
                if (!Required.Contains(slashed)) continue;
                // No line means Mojang never obfuscated it, so it is loaded
                // under the name it already has.
                var runtimeName = tiny.Classes.GetValueOrDefault(entry.ObfName, entry.ObfName);
                var mapped = new MappedClass(slashed, entry.ObfName, runtimeName);
                foreach (var (fieldName, obfField) in entry.Fields)
                {
                    mapped.Fields[fieldName] = new MappedMember(
                        fieldName,
                        obfField,
                        tiny.Fields.GetValueOrDefault(TinyKey(entry.ObfName, obfField), obfField),
                        string.Empty);
                }
                foreach (var member in entry.Methods)
                {
                    var descriptor = ObfDescriptorOf(member, proguard);
                    var name = member.ObfName;
                    var runtimeMember = name;
                    // Up the whole hierarchy, interfaces included: intermediary
                    // names a method once, at whatever declares it, and plenty
                    // of what a ServerPlayer answers is declared on an
                    // interface rather than on a class above it. getUUID is one
                    // of those, and looking only at superclasses left it
                    // obfuscated.
                    foreach (var owner in hierarchy.SupertypesOf(entry.ObfName))
                    {
                        if (tiny.Methods.TryGetValue(TinyKey(owner, name, descriptor), out var found))
                        {
                            runtimeMember = found;
                            break;
                        }
                    }
                    mapped.Methods.Add(new MappedMember(member.Official, name, runtimeMember, descriptor));
                }
                classes[slashed] = mapped;
            }
            return new RuntimeNames(classes);
        }

        private static string TinyKey(string owner, string name) => owner + " " + name;

        private static string TinyKey(string owner, string name, string descriptor) =>
            owner + " " + descriptor + " " + name;

        /// <summary>
        /// mappings/mappings.tiny, which opens "v1 official intermediary" and
        /// then says CLASS, FIELD or METHOD on every line, tab separated. The
        /// descriptors in it are obfuscated, which is what makes it joinable
        /// with Mojang's file at all.
        /// </summary>
        private static TinyMappings ReadTinyV1(string jarPath)
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var entry = archive.GetEntry("mappings/mappings.tiny")
                ?? throw new InvalidDataException("The intermediary jar carries no mappings.");
            using var reader = new StreamReader(entry.Open());
            var header = reader.ReadLine();
            if (header is null || !header.StartsWith("v1\t", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Intermediary mappings are not tiny v1.");
            }

            var tiny = new TinyMappings();
            for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
            {
                var parts = line.Split('\t');
                switch (parts[0])
                {
                    case "CLASS" when parts.Length >= 3:
                        tiny.Classes[parts[1]] = parts[2];
                        break;
                    case "FIELD" when parts.Length >= 5:
                        tiny.Fields[TinyKey(parts[1], parts[3])] = parts[4];
                        break;
                    case "METHOD" when parts.Length >= 5:
                        tiny.Methods[TinyKey(parts[1], parts[3], parts[2])] = parts[4];
                        break;
                }
            }
            return tiny;
        }

        private sealed class TinyMappings
        {
            public Dictionary<string, string> Classes { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, string> Methods { get; } = new(StringComparer.Ordinal);
        }

        /// <summary>
        /// What a class is, all the way up: itself, what it extends and what it
        /// implements, and the same again for each of those.
        ///
        /// Only the top of a class file is wanted - its own name, its
        /// superclass and its interfaces - so the constant pool is walked far
        /// enough to reach them and no further. A class that is not in the jar,
        /// or one that cannot be read, ends that branch of the walk rather than
        /// failing it.
        /// </summary>
        private sealed class JarHierarchy : IDisposable
        {
            private readonly ZipArchive? _archive;
            private readonly Dictionary<string, IReadOnlyList<string>> _supers = new(StringComparer.Ordinal);

            public JarHierarchy(string jarPath)
            {
                try
                {
                    _archive = ZipFile.OpenRead(jarPath);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    _archive = null;
                }
            }

            /// <summary>The class itself and everything above it, nearest first.</summary>
            public IEnumerable<string> SupertypesOf(string className)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var queue = new Queue<string>();
                queue.Enqueue(className);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (!seen.Add(current)) continue;
                    yield return current;
                    foreach (var parent in SupertypesRead(current))
                    {
                        if (!seen.Contains(parent)) queue.Enqueue(parent);
                    }
                }
            }

            private IReadOnlyList<string> SupertypesRead(string className)
            {
                if (_supers.TryGetValue(className, out var cached)) return cached;
                var found = ReadSupertypes(className);
                _supers[className] = found;
                return found;
            }

            private IReadOnlyList<string> ReadSupertypes(string className)
            {
                if (_archive?.GetEntry(className + ".class") is not { } entry) return [];
                try
                {
                    using var stream = entry.Open();
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    return SupertypeNamesOf(memory.ToArray());
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException)
                {
                    return [];
                }
            }

            private static IReadOnlyList<string> SupertypeNamesOf(byte[] data)
            {
                var offset = 8;
                var count = ReadUInt16(data, offset);
                offset += 2;
                var utf8 = new Dictionary<int, string>();
                var classes = new Dictionary<int, int>();
                for (var index = 1; index < count; index++)
                {
                    var tag = data[offset++];
                    switch (tag)
                    {
                        case 1:
                            var length = ReadUInt16(data, offset);
                            utf8[index] = System.Text.Encoding.UTF8.GetString(data, offset + 2, length);
                            offset += 2 + length;
                            break;
                        case 7:
                            classes[index] = ReadUInt16(data, offset);
                            offset += 2;
                            break;
                        case 8:
                        case 16:
                        case 19:
                        case 20:
                            offset += 2;
                            break;
                        case 15:
                            offset += 3;
                            break;
                        case 5:
                        case 6:
                            offset += 8;
                            index++;
                            break;
                        default:
                            offset += 4;
                            break;
                    }
                }
                // access_flags, this_class, super_class, then the interfaces.
                var names = new List<string>();
                void Take(int index)
                {
                    if (index != 0 && classes.TryGetValue(index, out var nameIndex) &&
                        utf8.GetValueOrDefault(nameIndex) is { } name)
                    {
                        names.Add(name);
                    }
                }
                Take(ReadUInt16(data, offset + 4));
                var interfaces = ReadUInt16(data, offset + 6);
                for (var index = 0; index < interfaces; index++)
                {
                    Take(ReadUInt16(data, offset + 8 + (index * 2)));
                }
                return names;
            }

            private static int ReadUInt16(byte[] data, int offset) => (data[offset] << 8) | data[offset + 1];

            public void Dispose() => _archive?.Dispose();
        }

        /// <summary>
        /// Mojang's file and nothing else, for a runtime that loads the game
        /// under the names Mojang gave it.
        /// </summary>
        /// <remarks>
        /// That is every NeoForge since 1.20.2, and it used to be answered by
        /// the merged mappings its installer wrote. The installer stopped
        /// writing them inside the 21.10 line, and there is nothing else in
        /// such a runtime to read - but there is nothing else needed either,
        /// because the name to load by is the name we asked for. Only the
        /// obfuscated one has to be looked up, and that is what Mojang's file
        /// is.
        /// </remarks>
        public static RuntimeNames ReadOfficialOnly(string proguardPath)
        {
            var proguard = ReadProguard(proguardPath);
            var classes = new Dictionary<string, MappedClass>(StringComparer.Ordinal);
            foreach (var (official, entry) in proguard)
            {
                var slashed = official.Replace('.', '/');
                if (!Required.Contains(slashed)) continue;
                var mapped = new MappedClass(slashed, entry.ObfName, slashed);
                foreach (var (fieldName, obfField) in entry.Fields)
                {
                    mapped.Fields[fieldName] = new MappedMember(fieldName, obfField, fieldName, string.Empty);
                }
                foreach (var member in entry.Methods)
                {
                    mapped.Methods.Add(new MappedMember(
                        member.Official,
                        member.ObfName,
                        member.Official,
                        ObfDescriptorOf(member, proguard)));
                }
                classes[slashed] = mapped;
            }
            return new RuntimeNames(classes);
        }

        public MappedClass RequireClass(string official) => _classes.TryGetValue(official, out var mapping)
            ? mapping
            : throw new InvalidDataException($"Required identity mapping class is missing: {official}");

        /// <summary>
        /// A class this build would use if the game has it. Everything the
        /// adapter must have is required above; anything asked for here is one
        /// feature, and a version without it keeps all the others rather than
        /// losing the adapter whole.
        /// </summary>
        public MappedClass? FindClass(string official) =>
            _classes.TryGetValue(official, out var mapping) ? mapping : null;

        private static List<Tsrg2Row> ReadTsrg2(string path)
        {
            var rows = new List<Tsrg2Row>();
            Tsrg2Row? current = null;
            var first = true;
            foreach (var line in File.ReadLines(path))
            {
                if (first)
                {
                    first = false;
                    if (!line.StartsWith("tsrg2 ", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("Identity mappings are not TSRG2.");
                    }
                    continue;
                }
                if (line.Length == 0) continue;
                if (line[0] != '\t')
                {
                    var classParts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    current = classParts.Length >= 2 && Required.Contains(classParts[1])
                        ? new Tsrg2Row(classParts[0], classParts[1])
                        : null;
                    if (current is not null) rows.Add(current);
                    continue;
                }
                if (current is null || line.StartsWith("\t\t", StringComparison.Ordinal)) continue;
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    current.Fields.Add(new Tsrg2Field(parts[0], parts[1]));
                }
                else if (parts.Length >= 3 && parts[1].StartsWith('('))
                {
                    current.Methods.Add(new Tsrg2Method(parts[0], parts[1], parts[2]));
                }
            }
            return rows;
        }

        /// <summary>
        /// Mojang's own mappings, as published beside every version: a class
        /// line "net.minecraft.Foo -&gt; abc:" and, indented under it, members
        /// written "12:15:int bar(float) -&gt; a". The line numbers in front are
        /// dropped; the return type is not, because an obfuscator gives two
        /// methods the same name and different return types and one of the two
        /// is usually the bridge.
        /// </summary>
        private static Dictionary<string, ProguardClass> ReadProguard(string path)
        {
            var classes = new Dictionary<string, ProguardClass>(StringComparer.Ordinal);
            ProguardClass? current = null;
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                if (!char.IsWhiteSpace(line[0]))
                {
                    var arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
                    if (arrow < 0) continue;
                    var official = line[..arrow];
                    if (!Required.Contains(official.Replace('.', '/')))
                    {
                        current = null;
                        // Kept anyway, with nothing in it: a descriptor names
                        // classes this build never asks about, and translating
                        // one needs their obfuscated names.
                        classes[official] = new ProguardClass(line[(arrow + 4)..].TrimEnd(':').Replace('.', '/'));
                        continue;
                    }
                    current = new ProguardClass(line[(arrow + 4)..].TrimEnd(':').Replace('.', '/'));
                    classes[official] = current;
                    continue;
                }
                if (current is null) continue;
                var body = line.Trim();
                var split = body.IndexOf(" -> ", StringComparison.Ordinal);
                if (split < 0) continue;
                var obfName = body[(split + 4)..];
                var signature = body[..split];
                var colon = signature.LastIndexOf(':');
                if (colon >= 0) signature = signature[(colon + 1)..];
                var space = signature.IndexOf(' ');
                if (space < 0) continue;
                var returnType = signature[..space];
                var rest = signature[(space + 1)..];
                var open = rest.IndexOf('(');
                if (open < 0)
                {
                    current.Fields[rest] = obfName;
                    continue;
                }
                var name = rest[..open];
                var arguments = rest[(open + 1)..].TrimEnd(')');
                var parameters = arguments.Length == 0
                    ? []
                    : arguments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                current.Methods.Add(new ProguardMethod(name, obfName, returnType, parameters));
            }
            return classes;
        }

        private sealed record Tsrg2Field(string LeftName, string RightName);

        private sealed record Tsrg2Method(string LeftName, string ObfDescriptor, string RightName);

        private sealed class Tsrg2Row
        {
            public Tsrg2Row(string leftName, string rightName)
            {
                LeftName = leftName;
                RightName = rightName;
            }

            public string LeftName { get; }
            public string RightName { get; }
            public List<Tsrg2Field> Fields { get; } = [];
            public List<Tsrg2Method> Methods { get; } = [];
        }

        private sealed class ProguardClass
        {
            public ProguardClass(string obfName) => ObfName = obfName;

            public string ObfName { get; }
            public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
            public List<ProguardMethod> Methods { get; } = [];
        }

        private sealed record ProguardMethod(
            string Official,
            string ObfName,
            string ReturnType,
            string[] Parameters);
    }
}

internal sealed record IdentityAdapterConfiguration(
    string MappingPath,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<IdentityAdapterTarget> Targets);

internal sealed record IdentityAdapterTarget(string JarPath, string ClassName);

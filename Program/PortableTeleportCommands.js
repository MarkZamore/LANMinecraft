// Launcher-managed KubeJS commands. Installed into each prepared game instance.
// The waypoint command names are intentionally private implementation details shared
// with the Xaero client hook. Every command can only move its invoking player.

const PORTABLE_WAYPOINT_COMMAND = 'portable_waypoint_tp'
const PORTABLE_WAYPOINT_DIMENSION_COMMAND = 'portable_waypoint_tp_dimension'
const PORTABLE_NETHER_DIMENSION = 'minecraft:the_nether'
const PORTABLE_HORIZONTAL_LIMIT = 29999984

function portableTeleportFailure(source, message) {
  source.sendFailure(Text.red(message))
  return 0
}

// Mirrors the per-world FTB Essentials policy the launcher writes for /spawn:
// a survival world that disallows commands keeps its convenience teleports
// off. The waypoint commands stay available because the map UI rides them.
function portableCasualTeleportBlocked(source) {
  try {
    const server = source.server
    if (server == null) {
      return false
    }
    return String(server.defaultGameType.getName()) === 'survival' &&
      !server.worldData.allowCommands
  } catch (error) {
    return false
  }
}

function portableFiniteNumber(value) {
  return typeof value === 'number' && Number.isFinite(value)
}

function portableValidRotation(value) {
  return portableFiniteNumber(value) && Math.abs(value) <= 36000000
}

function portableValidDestination(level, x, y, z) {
  if (level == null ||
      !portableFiniteNumber(x) ||
      !portableFiniteNumber(y) ||
      !portableFiniteNumber(z) ||
      Math.abs(x) > PORTABLE_HORIZONTAL_LIMIT ||
      Math.abs(z) > PORTABLE_HORIZONTAL_LIMIT) {
    return false
  }

  const minimumY = Number(level.minBuildHeight)
  const maximumY = Number(level.maxBuildHeight)
  const worldBorder = level.worldBorder
  return portableFiniteNumber(minimumY) &&
    portableFiniteNumber(maximumY) &&
    worldBorder != null &&
    worldBorder.isWithinBounds(x, z) &&
    y >= minimumY &&
    y < maximumY
}

function portableTeleportPlayer(source, level, x, y, z, yaw, pitch) {
  const player = source.player
  if (player == null) {
    return portableTeleportFailure(source, 'Эта команда доступна только игроку.')
  }
  if (!portableValidDestination(level, x, y, z) ||
      !portableValidRotation(yaw) ||
      !portableValidRotation(pitch)) {
    return portableTeleportFailure(source, 'Некорректная точка телепортации.')
  }

  player.teleportTo(level.dimension, x, y, z, yaw, pitch)
  return 1
}

function portableTeleportSharedSpawn(source, level) {
  const player = source.player
  if (player == null || level == null) {
    return portableTeleportFailure(source, 'Измерение недоступно.')
  }

  const spawn = level.sharedSpawnPos
  const x = Number(spawn.x) + 0.5
  const y = Number(spawn.y) + 0.1
  const z = Number(spawn.z) + 0.5
  const yaw = Number(level.sharedSpawnAngle)
  if (!portableFiniteNumber(x) ||
      !portableFiniteNumber(y) ||
      !portableFiniteNumber(z) ||
      !portableValidRotation(yaw)) {
    return portableTeleportFailure(source, 'Некорректная точка появления измерения.')
  }

  // ServerLevel.sharedSpawnPos is world-owned, so every player is sent to
  // exactly the same Nether spawn instead of a position derived from the caller.
  player.teleportTo(
    level,
    x,
    y,
    z,
    yaw,
    0
  )
  return 1
}

ServerEvents.commandRegistry(event => {
  const Commands = event.commands
  const Arguments = event.arguments

  const currentDimensionCommand = Commands.literal(PORTABLE_WAYPOINT_COMMAND)
    .requires(source => source.player != null)
    .then(Commands.argument('x', Arguments.DOUBLE.create(event))
      .then(Commands.argument('y', Arguments.DOUBLE.create(event))
        .then(Commands.argument('z', Arguments.DOUBLE.create(event))
          .executes(context => {
            const player = context.source.player
            return portableTeleportPlayer(
              context.source,
              player.level,
              Arguments.DOUBLE.getResult(context, 'x'),
              Arguments.DOUBLE.getResult(context, 'y'),
              Arguments.DOUBLE.getResult(context, 'z'),
              Number(player.yaw),
              Number(player.pitch)
            )
          })
          .then(Commands.argument('yaw', Arguments.FLOAT.create(event))
            .executes(context => {
              const player = context.source.player
              return portableTeleportPlayer(
                context.source,
                player.level,
                Arguments.DOUBLE.getResult(context, 'x'),
                Arguments.DOUBLE.getResult(context, 'y'),
                Arguments.DOUBLE.getResult(context, 'z'),
                Arguments.FLOAT.getResult(context, 'yaw'),
                Number(player.pitch)
              )
            })))))

  const crossDimensionCommand = Commands.literal(PORTABLE_WAYPOINT_DIMENSION_COMMAND)
    .requires(source => source.player != null)
    .then(Commands.argument('dimension', Arguments.DIMENSION.create(event))
      .then(Commands.argument('x', Arguments.DOUBLE.create(event))
        .then(Commands.argument('y', Arguments.DOUBLE.create(event))
          .then(Commands.argument('z', Arguments.DOUBLE.create(event))
            .executes(context => {
              const player = context.source.player
              return portableTeleportPlayer(
                context.source,
                Arguments.DIMENSION.getResult(context, 'dimension'),
                Arguments.DOUBLE.getResult(context, 'x'),
                Arguments.DOUBLE.getResult(context, 'y'),
                Arguments.DOUBLE.getResult(context, 'z'),
                Number(player.yaw),
                Number(player.pitch)
              )
            })
            .then(Commands.argument('yaw', Arguments.FLOAT.create(event))
              .executes(context => {
                const player = context.source.player
                return portableTeleportPlayer(
                  context.source,
                  Arguments.DIMENSION.getResult(context, 'dimension'),
                  Arguments.DOUBLE.getResult(context, 'x'),
                  Arguments.DOUBLE.getResult(context, 'y'),
                  Arguments.DOUBLE.getResult(context, 'z'),
                  Arguments.FLOAT.getResult(context, 'yaw'),
                  Number(player.pitch)
                )
              }))))))

  const netherCommand = Commands.literal('nether')
    .requires(source => source.player != null && !portableCasualTeleportBlocked(source))
    .executes(context => {
      const player = context.source.player
      const level = player.server.getLevel(PORTABLE_NETHER_DIMENSION)
      if (level == null) {
        return portableTeleportFailure(
          context.source,
          'Измерение minecraft:the_nether недоступно.'
        )
      }
      return portableTeleportSharedSpawn(context.source, level)
    })

  event.register(currentDimensionCommand)
  event.register(crossDimensionCommand)
  event.register(netherCommand)
})

--[[
  🚨 AI Police Pursuit Mode v1.0
  by Cracker

  Cops patrol the streets. Break the speed limit near one,
  and they'll flip the sirens on and hunt you down.
  Outrun them or get BUSTED.
]]

----------------------------------------------------------------------
-- SETTINGS
----------------------------------------------------------------------
local settings = ac.INIConfig.scriptSettings():mapSection('SETTINGS', {
  SPEED_THRESHOLD    = 120,
  DETECTION_RADIUS   = 250,
  ARREST_DISTANCE    = 6,
  ARREST_TIME        = 3,
  ESCAPE_DISTANCE    = 800,
  ESCAPE_TIME        = 30,
  RUBBERBAND_DISTANCE = 600,
  HEAT_LEVELS        = 5,
  COP_MAX_SPEED      = 280,
  DEBUG_MODE         = false,
})

----------------------------------------------------------------------
-- CONSTANTS
----------------------------------------------------------------------
local PHASE_PATROL    = 0
local PHASE_DETECTED  = 1
local PHASE_PURSUIT   = 2
local PHASE_BUSTED    = 3
local PHASE_ESCAPED   = 4

local PHASE_NAMES = {
  [PHASE_PATROL]   = 'PATROL',
  [PHASE_DETECTED] = 'DETECTED!',
  [PHASE_PURSUIT]  = 'IN PURSUIT',
  [PHASE_BUSTED]   = 'BUSTED!',
  [PHASE_ESCAPED]  = 'ESCAPED',
}

local PHASE_COLORS = {
  [PHASE_PATROL]   = rgbm(0.3, 0.8, 0.3, 1),
  [PHASE_DETECTED] = rgbm(1.0, 0.8, 0.0, 1),
  [PHASE_PURSUIT]  = rgbm(1.0, 0.15, 0.15, 1),
  [PHASE_BUSTED]   = rgbm(1.0, 0.0, 0.0, 1),
  [PHASE_ESCAPED]  = rgbm(0.2, 0.6, 1.0, 1),
}

-- Known police car IDs (folder names)
local POLICE_CAR_IDS = {
  ['pb_ford_crownvic_cop'] = true,
  ['bara_dodge_charger_sxt_2014'] = true,
  ['nolimits_traffic_ford_victoria_2011_full'] = true,
}

----------------------------------------------------------------------
-- STATE
----------------------------------------------------------------------
local sim = ac.getSim()
local phase = PHASE_PATROL
local heatLevel = 0
local cops = {}                   -- array of { index, pursuing, arrestTimer, sirenOn }
local playerIndex = 0             -- player is always car 0
local escapeTimer = 0
local detectedTimer = 0
local bustedTimer = 0
local escapedDisplayTimer = 0
local closestCopDist = math.huge
local bustedFlashTimer = 0
local initialized = false
local clock = 0                   -- accumulated seconds, for cooldowns

-- Audio
local sirenAudio = nil

-- AI spline paths for the track
local aiSplines = {}

-- Where the player has driven, oldest first. Cops follow this.
local TRAIL_SPACING = 4       -- meters between points
local TRAIL_MAX = 500         -- 2 km of history
local TRAIL_MAX_OFFSET = 30   -- further than this a cop is not on the trail
local trail = {}
local steerSign = 1
local steerEvidence = 0

----------------------------------------------------------------------
-- HELPER FUNCTIONS
----------------------------------------------------------------------
local function distXZ(a, b)
  local dx = a.x - b.x
  local dz = a.z - b.z
  return math.sqrt(dx * dx + dz * dz)
end

local function lerpColor(a, b, t)
  t = math.max(0, math.min(1, t))
  return rgbm(
    a.r + (b.r - a.r) * t,
    a.g + (b.g - a.g) * t,
    a.b + (b.b - a.b) * t,
    a.mult + (b.mult - a.mult) * t
  )
end

----------------------------------------------------------------------
-- INITIALIZATION
----------------------------------------------------------------------
local function discoverCops()
  cops = {}
  local count = sim.carsCount
  for i = 1, count - 1 do  -- skip car 0 (player)
    local carId = ac.getCarID(i)
    if POLICE_CAR_IDS[carId] then
      local order = #cops  -- 0-based formation slot
      -- Formation spread: 0, -0.35, +0.35, -0.7, +0.7, ... so units
      -- approach in a wall instead of a single-file train
      local spread = math.ceil(order / 2) * 0.35 * (order % 2 == 1 and -1 or 1)
      table.insert(cops, {
        index = i,
        order = order,
        spread = math.max(-0.85, math.min(0.85, spread)),
        pursuing = false,
        hunting = false,
        arrestTimer = 0,
        sirenOn = false,
        lastRubberbandTime = 0,
        stuckTimer = 0,
        distance = math.huge,
      })
      ac.log('[POLICE] Found cop car: ' .. carId .. ' at index ' .. i)
    end
  end
  ac.log('[POLICE] Discovered ' .. #cops .. ' cop cars')
end

local function discoverSplines()
  aiSplines = {}
  local trackDir = ac.getFolder(ac.FolderID.CurrentTrackLayout)
  -- Try common spline names
  local splineNames = { 'fast_lane.ai', 'fast_lane1.ai', 'fast_lane2.ai', 'fast_lane3.ai', 'pit_lane.ai' }
  for _, name in ipairs(splineNames) do
    local path = trackDir .. '/ai/' .. name
    if io.fileExists(path) then
      table.insert(aiSplines, path)
      ac.log('[POLICE] Found AI spline: ' .. name)
    end
  end
  if #aiSplines == 0 then
    -- Fallback: check data directory
    local dataDir = trackDir .. '/data'
    for _, name in ipairs(splineNames) do
      local path = dataDir .. '/' .. name
      if io.fileExists(path) then
        table.insert(aiSplines, path)
      end
    end
  end
  ac.log('[POLICE] Total AI splines: ' .. #aiSplines)
end

local function updateTrail(player)
  local p = player.position
  local last = trail[#trail]
  if last and distXZ(last, p) > 50 then  -- player was teleported
    trail = {}
    last = nil
  end
  if not last or distXZ(last, p) >= TRAIL_SPACING then
    table.insert(trail, vec3(p.x, p.y, p.z))
    if #trail > TRAIL_MAX then table.remove(trail, 1) end
  end
end

local function startHunt(cop)
  cop.hunting = true
  cop.steer = 0
  cop.lastGas = 0
  cop.blockedTimer = 0
  cop.deadTimer = 0
  cop.reversing = false
  cop.leftReverse = false
  cop.aiPedals = false
  cop.prevFwdX = nil
  physics.setCarAutoShifter(cop.index, true)
  physics.setAINoInput(cop.index, true)
end

local function stopHunt(cop)
  if not cop.hunting then return end
  cop.hunting = false
  local ctl = ac.overrideCarControls(cop.index)
  if ctl then
    ctl.steer = math.huge
    ctl.gas = 0
    ctl.brake = 0
    ctl.horn = false
    ctl.requestedGearIndex = 0
  end
  physics.setCarAutoShifter(cop.index, false)
  physics.setAINoInput(cop.index, false)  -- give the wheel back to the AI
end

local function setCopPatrol(cop)
  local ci = cop.index
  stopHunt(cop)
  physics.setAINoInput(ci, false)  -- give the wheel back to the AI
  physics.setAILevel(ci, 0.85)
  physics.setAIAggression(ci, 0.3)
  physics.setAICaution(ci, 8)
  -- Varied cruise speeds so units disperse across the map over time
  physics.setAITopSpeed(ci, 65 + (cop.order % 4) * 10)
  physics.setAIThrottleLimit(ci, 0.7)
  physics.setAISplineOffset(ci, 0, false)  -- back to normal lane keeping
  physics.setExtraAIGrip(ci, 1.2)
  physics.preventAIFromRetiring(ci)
  physics.setAIPitStopRequest(ci, false)
  physics.setAIStopCounter(ci, 0)  -- release incident/waiting brake
end

-- Without CSP's AI flood, Track Day AI sit in the pits waiting for a
-- dispatcher that no longer exists — deploy them onto the spline around
-- the player ourselves, spread out at staggered distances.
local function deployCops()
  local player = ac.getCar(playerIndex)
  if not player then return end
  local trackLen = math.max(1000, sim.trackLengthM or 10000)
  local playerProg = ac.worldCoordinateToTrackProgress(player.position)

  for _, cop in ipairs(cops) do
    local prog = (playerProg + (200 + cop.order * 300) / trackLen) % 1
    local pos = ac.trackProgressToWorldCoordinate(prog)
    local ahead = ac.trackProgressToWorldCoordinate((prog + 10 / trackLen) % 1)
    physics.setAICarPosition(cop.index, pos, (ahead - pos):normalize())
    physics.awakeCar(cop.index)
    physics.setAIStopCounter(cop.index, 0)
    ac.log('[POLICE] Deployed cop ' .. cop.index .. ' onto patrol route')
  end
end

-- Respawn a cop on the AI spline N meters behind the player, facing
-- along it. Used for rubberbanding and for un-sticking frozen cops.
local function respawnCopBehind(cop, metersBehind)
  local player = ac.getCar(playerIndex)
  if not player then return end
  local trackLen = math.max(1000, sim.trackLengthM or 10000)
  local playerProg = ac.worldCoordinateToTrackProgress(player.position)

  -- In free roam the player may be driving AGAINST the spline direction.
  -- Spawn behind their actual MOTION and face them along their direction
  -- of travel — otherwise respawned cops appear ahead, facing away, and
  -- instantly fall behind again.
  local t0 = ac.trackProgressToWorldCoordinate(playerProg)
  local t1 = ac.trackProgressToWorldCoordinate((playerProg + 10 / trackLen) % 1)
  local tangent = (t1 - t0):normalize()
  local dir = player.velocity
  if dir:length() < 3 then dir = player.look end  -- stationary: use facing
  local dirSign = (dir.x * tangent.x + dir.z * tangent.z) >= 0 and 1 or -1

  local spawnProg = (playerProg - dirSign * metersBehind / trackLen) % 1
  local spawnPos = ac.trackProgressToWorldCoordinate(spawnProg)
  local aheadPos = ac.trackProgressToWorldCoordinate((spawnProg + dirSign * 10 / trackLen) % 1)
  physics.setAICarPosition(cop.index, spawnPos, (aheadPos - spawnPos):normalize())
  physics.awakeCar(cop.index)
  physics.setAIStopCounter(cop.index, 0)
  cop.lastRubberbandTime = clock
  cop.stuckTimer = 0
  cop.prevFwdX = nil  -- avoid a yaw-rate spike after teleporting
  cop.reversing = false
  cop.blockedTimer = 0
end

-- Cops sometimes freeze (AI brain gives up after teleports/collisions);
-- respawning them into the action fixes it, so do that automatically.
local function checkStuck(cop, copCar, dt)
  local flipped = copCar.up.y < 0.6
  if copCar.speedKmh < 3 or flipped then
    cop.stuckTimer = cop.stuckTimer + dt
  else
    cop.stuckTimer = 0
  end
  -- Don't respawn a close cop (it may be pinning the player) — unless
  -- it's flipped over, in which case it's a wreck: recycle it
  if cop.stuckTimer > 4 and (cop.distance > 25 or flipped) then
    ac.log('[POLICE] Cop ' .. cop.index .. ' is stuck/wrecked, respawning')
    respawnCopBehind(cop, 120 + cop.order * 30)
  end
end

local function initialize()
  if initialized then return end
  initialized = true

  -- RACE base mode nags about driving against spline direction —
  -- meaningless in free-roam pursuit, so discard those messages.
  ac.blockSystemMessages('rong way')

  discoverCops()
  discoverSplines()

  -- Set all cops to patrol mode initially
  for _, cop in ipairs(cops) do
    setCopPatrol(cop)
  end
end

----------------------------------------------------------------------
-- SIREN CONTROL
----------------------------------------------------------------------
local function setSirenOn(cop, on)
  if cop.sirenOn == on then return end
  cop.sirenOn = on

  -- Use ac.setTargetCar + ac.setExtraSwitch to toggle Extra A (siren lights)
  -- For Crown Vic, the horn IS the siren (HORN_AS_SIREN=1), and emissives are on HORN input
  -- We also set Extra Switch 0 if available
  if ac.setTargetCar(cop.index) then
    ac.setExtraSwitch(0, on)
    -- Also try toggling headlights and high beams for extra drama
    if on then
      ac.setHighBeams(true)
    end
  end
  -- Reset target back to player
  ac.setTargetCar(playerIndex)
end

----------------------------------------------------------------------
-- PATROL PHASE — cops chill on their AI splines
----------------------------------------------------------------------
local function updatePatrol(dt)
  local player = ac.getCar(playerIndex)
  if not player then return end

  local playerSpeed = player.speedKmh
  local playerPos = player.position

  -- Check if player is speeding near any cop
  for _, cop in ipairs(cops) do
    local copCar = ac.getCar(cop.index)
    if copCar and copCar.isActive then
      cop.distance = distXZ(playerPos, copCar.position)
      checkStuck(cop, copCar, dt)

      if playerSpeed > settings.SPEED_THRESHOLD and cop.distance < settings.DETECTION_RADIUS then
        -- DETECTED!
        phase = PHASE_DETECTED
        detectedTimer = 0
        heatLevel = math.max(1, heatLevel)
        ac.log('[POLICE] Player detected speeding at ' .. math.floor(playerSpeed) .. ' km/h!')
        return
      end
    end
  end

  -- Keep cops in patrol mode
  for _, cop in ipairs(cops) do
    physics.preventAIFromRetiring(cop.index)
    physics.setAIStopCounter(cop.index, 0)
  end
end

----------------------------------------------------------------------
-- DETECTED PHASE — brief alert before full pursuit (1.5s dramatic pause)
----------------------------------------------------------------------
local function updateDetected(dt)
  detectedTimer = detectedTimer + dt

  -- Start activating sirens on nearby cops
  local player = ac.getCar(playerIndex)
  if not player then return end

  for _, cop in ipairs(cops) do
    local copCar = ac.getCar(cop.index)
    if copCar and copCar.isActive then
      cop.distance = distXZ(player.position, copCar.position)
      if cop.distance < settings.DETECTION_RADIUS * 1.5 then
        setSirenOn(cop, true)
        cop.pursuing = true
      end
    end
  end

  -- After 1.5 seconds, go to full pursuit
  if detectedTimer >= 1.5 then
    phase = PHASE_PURSUIT
    ac.log('[POLICE] PURSUIT ENGAGED!')
  end
end

----------------------------------------------------------------------
-- PURSUIT PHASE — cops go full aggro
----------------------------------------------------------------------
local function updatePursuit(dt)
  local player = ac.getCar(playerIndex)
  if not player then return end

  local playerPos = player.position
  local playerSpeed = player.speedKmh

  -- Update heat level based on speed
  local speedOver = playerSpeed - settings.SPEED_THRESHOLD
  if speedOver > 100 then
    heatLevel = math.min(settings.HEAT_LEVELS, math.max(heatLevel, 5))
  elseif speedOver > 70 then
    heatLevel = math.min(settings.HEAT_LEVELS, math.max(heatLevel, 4))
  elseif speedOver > 40 then
    heatLevel = math.min(settings.HEAT_LEVELS, math.max(heatLevel, 3))
  elseif speedOver > 20 then
    heatLevel = math.min(settings.HEAT_LEVELS, math.max(heatLevel, 2))
  end

  -- Track closest cop
  closestCopDist = math.huge
  local allCopsFarAway = true
  local anyPursuing = false

  for _, cop in ipairs(cops) do
    local copCar = ac.getCar(cop.index)
    if not copCar or not copCar.isActive then goto continue end

    cop.distance = distXZ(playerPos, copCar.position)
    if cop.distance < closestCopDist then
      closestCopDist = cop.distance
    end
    checkStuck(cop, copCar, dt)

    -- Activate ALL cops if heat is high enough
    if not cop.pursuing and heatLevel >= 2 then
      cop.pursuing = true
      setSirenOn(cop, true)
    end

    if cop.pursuing then
      anyPursuing = true
      physics.preventAIFromRetiring(cop.index)
      physics.setAIPitStopRequest(cop.index, false)
      -- Ramming counts as an incident: without this, AI freeze after contact
      physics.setAIStopCounter(cop.index, 0)

      -- Base AI tuning scaled with heat
      physics.setAILevel(cop.index, 1.0)
      physics.setAIAggression(cop.index, 1.0)
      physics.setAICaution(cop.index, 0)
      physics.setAIThrottleLimit(cop.index, 1.0)
      physics.setExtraAIGrip(cop.index, 1.2 + heatLevel * 0.04)

      -- ===== CHASE CONTROLLER =====
      -- Far away: AI drives the spline flat-out toward the player (it
      -- knows the roads). Close in or on the player's trail: HUNT MODE —
      -- AI input is cut and the cop drives the trail with real controls,
      -- since spline AI can never leave its line.
      local nearTrail = false
      for t = #trail, 1, -1 do
        if distXZ(trail[t], copCar.position) < 25 then
          nearTrail = true
          break
        end
      end
      if not cop.hunting and cop.distance < 200 and (cop.distance < 50 or nearTrail) then
        startHunt(cop)
      elseif cop.hunting and (cop.distance > 280 or (cop.distance > 100 and not nearTrail)) then
        stopHunt(cop)
      end

      if not cop.hunting then
        local playerTrack = ac.worldCoordinateToTrack(playerPos)
        local copTrack = ac.worldCoordinateToTrack(copCar.position)
        physics.setAISplineOffset(cop.index,
          math.max(-1, math.min(1, playerTrack.x + cop.spread)), true)

        -- Wrap-aware progress gap: flat out when behind, rolling
        -- roadblock when ahead of the player.
        local gap = playerTrack.z - copTrack.z
        if gap > 0.5 then gap = gap - 1 elseif gap < -0.5 then gap = gap + 1 end
        if gap >= 0 then
          physics.setAITopSpeed(cop.index, settings.COP_MAX_SPEED)
        else
          physics.setAITopSpeed(cop.index, math.max(30, playerSpeed * 0.4))
        end
      end

      -- RUBBERBANDING: If cop is too far behind, teleport them closer,
      -- each unit at its own staggered distance
      if cop.distance > settings.RUBBERBAND_DISTANCE
         and (clock - cop.lastRubberbandTime) > (8 + cop.order * 2) then
        respawnCopBehind(cop, 90 + cop.order * 35)
        ac.log('[POLICE] Rubberbanded cop ' .. cop.index .. ' to intercept position')
      end

      -- ARREST CHECK: Is cop close enough to pin the player?
      if cop.distance < settings.ARREST_DISTANCE then
        -- Only arrest if player is slow enough (indicating actual contact/pin)
        if playerSpeed < 30 then
          cop.arrestTimer = cop.arrestTimer + dt
          if cop.arrestTimer >= settings.ARREST_TIME then
            phase = PHASE_BUSTED
            bustedTimer = 0
            ac.log('[POLICE] BUSTED! Cop ' .. cop.index .. ' pinned the player!')
            return
          end
        else
          cop.arrestTimer = math.max(0, cop.arrestTimer - dt * 0.5)
        end
      else
        cop.arrestTimer = math.max(0, cop.arrestTimer - dt * 2)
      end

      -- If cop is within range, check for escape
      if cop.distance < settings.ESCAPE_DISTANCE then
        allCopsFarAway = false
      end
    end

    ::continue::
  end

  -- ESCAPE CHECK: All pursuing cops are far away
  if allCopsFarAway and anyPursuing then
    escapeTimer = escapeTimer + dt
    if escapeTimer >= settings.ESCAPE_TIME then
      phase = PHASE_ESCAPED
      escapedDisplayTimer = 0
      ac.log('[POLICE] Player ESCAPED!')
      return
    end
  else
    escapeTimer = math.max(0, escapeTimer - dt * 2)
  end
end

----------------------------------------------------------------------
-- BUSTED PHASE — game over sequence
----------------------------------------------------------------------
local function updateBusted(dt)
  bustedTimer = bustedTimer + dt
  bustedFlashTimer = bustedFlashTimer + dt

  -- Force the player to a stop while busted
  physics.setGentleStop(playerIndex, true)

  -- After 6 seconds, offer restart
  if bustedTimer > 6 then
    -- Reset for another round
    phase = PHASE_PATROL
    heatLevel = 0
    bustedTimer = 0
    escapeTimer = 0

    for _, cop in ipairs(cops) do
      cop.pursuing = false
      cop.arrestTimer = 0
      setSirenOn(cop, false)
      setCopPatrol(cop)
    end

    -- Release the player and teleport to pits
    physics.setGentleStop(playerIndex, false)
    physics.teleportCarTo(playerIndex, ac.SpawnSet.HotlapStart)
    ac.log('[POLICE] Session reset after BUSTED')
  end
end

----------------------------------------------------------------------
-- ESCAPED PHASE — brief celebration then reset
----------------------------------------------------------------------
local function updateEscaped(dt)
  escapedDisplayTimer = escapedDisplayTimer + dt

  -- Return all cops to patrol
  for _, cop in ipairs(cops) do
    if cop.pursuing then
      cop.pursuing = false
      cop.arrestTimer = 0
      setSirenOn(cop, false)
      setCopPatrol(cop)
    end
  end

  -- Gradually reduce heat
  if escapedDisplayTimer > 1 then
    heatLevel = math.max(0, heatLevel - 1)
  end

  -- After 4 seconds, back to patrol
  if escapedDisplayTimer > 4 then
    phase = PHASE_PATROL
    heatLevel = 0
    escapeTimer = 0
    closestCopDist = math.huge
  end
end

----------------------------------------------------------------------
-- MAIN UPDATE (game thread, every frame)
----------------------------------------------------------------------
local lastError = nil

local deployed = false
local driveHuntingCop  -- forward declaration, defined below

-- Player velocity smoothed over ~0.6s: cops chase this instead of the
-- instantaneous velocity, so quick left-right jinks average out to
-- "going straight" — the way a human reads an evading driver — instead
-- of being amplified into wild counter-swerves by the lead prediction.
local smPlayerVel = nil

local function updateInner(dt)
  initialize()

  if #cops == 0 then return end
  clock = clock + dt

  -- Deploy cops onto the road shortly after session start (give the
  -- session a few seconds to fully initialize AI first)
  if not deployed and clock > 3 then
    deployed = true
    deployCops()
  end

  if phase == PHASE_PATROL then
    updatePatrol(dt)
  elseif phase == PHASE_DETECTED then
    updateDetected(dt)
  elseif phase == PHASE_PURSUIT then
    updatePursuit(dt)
  elseif phase == PHASE_BUSTED then
    updateBusted(dt)
  elseif phase == PHASE_ESCAPED then
    updateEscaped(dt)
  end

  -- Hunt controller runs here (game thread): car state is fresh every
  -- frame, so the yaw-rate measurement is correct. On the physics
  -- thread the state is stale most ticks, which broke the servo.
  if phase == PHASE_PURSUIT then
    local player = ac.getCar(playerIndex)
    if player then
      if smPlayerVel == nil then
        smPlayerVel = vec3(player.velocity.x, 0, player.velocity.z)
      end
      updateTrail(player)
      local kv = math.min(1, dt / 0.6)
      smPlayerVel.x = smPlayerVel.x + (player.velocity.x - smPlayerVel.x) * kv
      smPlayerVel.z = smPlayerVel.z + (player.velocity.z - smPlayerVel.z) * kv

      for _, cop in ipairs(cops) do
        if cop.hunting then
          driveHuntingCop(cop, player, dt)
        end
      end
    end
  end
end

function script.update(dt)
  local ok, err = pcall(updateInner, dt)
  if not ok then
    lastError = tostring(err)
    ac.log('[POLICE] ERROR: ' .. lastError)
  end
end

----------------------------------------------------------------------
-- HUNT MODE DRIVER
-- Nearby pursuing cops have AI input cut and drive with real controls:
-- steering, throttle and brake through the tires. Nothing pushes the
-- body, so physics decides what the car can do and it stays on the road.
-- They follow the player's own breadcrumb trail, which is always a
-- drivable route, so they change lanes and take junctions like a tail car.
----------------------------------------------------------------------
local WHEELBASE = 2.9
local MAX_WHEEL_ANGLE = 0.5    -- radians at full steering input
local BRAKE_DECEL = 8          -- m/s², what we plan corner braking with

local function lateralBudget()
  return 8 + heatLevel * 0.4   -- m/s², cornering limit grows with heat
end

local function findOnTrail(pos)
  local bestIndex, bestDist = nil, math.huge
  for i = #trail, 1, -1 do
    local d = distXZ(trail[i], pos)
    if d < bestDist then
      bestIndex, bestDist = i, d
    end
  end
  return bestIndex, bestDist
end

local function pointAlongTrail(index, meters)
  local steps = math.ceil(meters / TRAIL_SPACING)
  return trail[math.min(#trail, index + steps)]
end

-- Fastest speed from which the cop can still brake for every corner the
-- player took in the stretch ahead (v² = vCorner² + 2·a·d)
local function trailSpeedLimit(index, speed)
  local limit = math.huge
  local reach = speed * speed / (2 * BRAKE_DECEL) + 20
  local step = 3
  local travelled = 0
  local i = index + step
  while i + step <= #trail and travelled < reach do
    local a, b, c = trail[i - step], trail[i], trail[i + step]
    local h1 = math.atan2(b.x - a.x, b.z - a.z)
    local h2 = math.atan2(c.x - b.x, c.z - b.z)
    local turn = math.abs(((h2 - h1 + math.pi) % (2 * math.pi)) - math.pi)
    local curvature = turn / (step * TRAIL_SPACING)
    local cornerSpeed = math.sqrt(lateralBudget() / math.max(curvature, 0.001))
    limit = math.min(limit, math.sqrt(cornerSpeed * cornerSpeed + 2 * BRAKE_DECEL * travelled))
    travelled = travelled + step * TRAIL_SPACING
    i = i + step
  end
  return limit
end

driveHuntingCop = function(cop, player, dt)
  local car = ac.getCar(cop.index)
  local ctl = ac.overrideCarControls(cop.index)
  if not car or not ctl then return end

  -- Rolled over: controls do nothing, the stuck detector will respawn it
  if car.up.y < 0.6 then return end

  local fwd, side, vel = car.look, car.side, car.velocity
  local fwdSpeed = vel.x * fwd.x + vel.z * fwd.z
  local speed = math.abs(fwdSpeed)

  -- Where to steer: along the player's trail when on it, otherwise straight
  -- at the player's predicted position to get onto it
  local lookahead = math.max(7, speed * 0.75)
  local trailIndex, trailDist = findOnTrail(car.position)
  local onTrail = trailIndex ~= nil and trailDist < TRAIL_MAX_OFFSET and cop.distance > 15
  local target
  if onTrail then
    target = pointAlongTrail(trailIndex, lookahead)
  else
    local tau = math.min(1.5, cop.distance / math.max(speed, 8))
    target = player.position + (smPlayerVel or player.velocity) * tau
  end

  local dx, dz = target.x - car.position.x, target.z - car.position.z
  local len = math.max(math.sqrt(dx * dx + dz * dz), 0.5)
  local localX = (dx * side.x + dz * side.z) / len     -- sin(angle), + = toward `side`
  local alignment = (dx * fwd.x + dz * fwd.z) / len    -- cos(angle)

  -- Target speed: catch up fast, ease off on approach, keep a small
  -- overspeed so contact stays a shove
  local boost
  if cop.distance > 120 then
    boost = 70
  elseif cop.distance > 40 then
    boost = 25 + (cop.distance - 40) * 0.55
  else
    boost = 6 + cop.distance * 0.45
  end
  local wantSpeed = math.min(settings.COP_MAX_SPEED, player.speedKmh + boost) / 3.6

  -- Lift for the corners ahead and for the arc being steered right now
  if onTrail then
    wantSpeed = math.min(wantSpeed, trailSpeedLimit(trailIndex, speed))
  end
  local arc = 2 * math.abs(localX) / math.max(len, 4)
  if arc > 1e-4 then
    wantSpeed = math.min(wantSpeed, math.sqrt(lateralBudget() / arc))
  end
  wantSpeed = wantSpeed * math.max(0.25, alignment)

  -- Blocked or pointing the wrong way at low speed: back up and swing round
  if car.speedKmh < 5 and cop.lastGas > 0.6 then
    cop.blockedTimer = cop.blockedTimer + dt
  else
    cop.blockedTimer = 0
  end
  if not cop.reversing and (cop.blockedTimer > 1.2 or (alignment < -0.25 and speed < 6)) then
    cop.reversing = true
    cop.reverseUntil = clock + 1.6
  elseif cop.reversing and (clock > cop.reverseUntil or (cop.blockedTimer == 0 and alignment > 0.5)) then
    cop.reversing = false
    cop.leftReverse = true
  end

  -- Pure pursuit steering, capped by what the tires can hold at this speed
  local wheelAngle = math.atan(2 * WHEELBASE * localX / math.max(len, 4))
  local gripAngle = math.atan(WHEELBASE * lateralBudget() / math.max(speed * speed, 1))
  local maxAngle = math.min(MAX_WHEEL_ANGLE, gripAngle)
  local wantSteer = math.clamp(wheelAngle, -maxAngle, maxAngle) / MAX_WHEEL_ANGLE
  if cop.reversing then wantSteer = localX >= 0 and -1 or 1 end
  local steerRate = (speed > 25 and 1.8 or 4) * dt     -- hands, not a servo
  cop.steer = cop.steer + math.clamp(wantSteer - cop.steer, -steerRate, steerRate)

  -- Steering sign is learned from how the car actually rotates, so a
  -- flipped axis fixes itself within a moment instead of spinning cops out
  if cop.prevFwdX and not cop.reversing and speed > 6 and math.abs(cop.steer) > 0.05 then
    local yawRate = math.atan2(fwd.x * cop.prevSideX + fwd.z * cop.prevSideZ,
      fwd.x * cop.prevFwdX + fwd.z * cop.prevFwdZ) / math.max(dt, 0.001)
    steerEvidence = math.min(2, steerEvidence + cop.steer * yawRate * dt)
    if steerEvidence < -1 then
      steerSign, steerEvidence = -steerSign, 0
      ac.log('[POLICE] Steering sign flipped to ' .. steerSign)
    end
  end
  cop.prevFwdX, cop.prevFwdZ = fwd.x, fwd.z
  cop.prevSideX, cop.prevSideZ = side.x, side.z

  local gas, brake = 0, 0
  if cop.reversing then
    gas = fwdSpeed > 1 and 0 or 0.6
    brake = fwdSpeed > 1 and 1 or 0
  else
    local err = wantSpeed - fwdSpeed
    gas = math.clamp(err * 0.25, 0, 1)
    if err < -1 then brake = math.clamp(-err * 0.15, 0, 1) end
    if fwdSpeed < -1 then gas, brake = 0, 1 end
  end

  ctl.steer = cop.steer * steerSign
  ctl.gas = gas
  ctl.brake = brake
  ctl.clutch = 1
  ctl.horn = true  -- horn is the siren on police cars
  ctl.requestedGearIndex = cop.reversing and -1 or (cop.leftReverse and 1 or 0)
  cop.leftReverse = false
  cop.lastGas = gas

  -- If the parked state ignores our pedals, hand them back to the AI brain
  -- and keep only the steering
  if not cop.aiPedals then
    if car.speedKmh > 5 then
      cop.deadTimer = 0
    else
      cop.deadTimer = cop.deadTimer + dt
      if cop.deadTimer > 5 then
        cop.aiPedals = true
        physics.setAINoInput(cop.index, false)
        ac.log('[POLICE] Cop ' .. cop.index .. ' ignored pedals, using AI pedals')
      end
    end
  end
end

local simDtLogged = false

function script.simUpdate(dt)
  if not simDtLogged then
    simDtLogged = true
    ac.log(string.format('[POLICE] simUpdate rate: dt=%.4fs (%.0f Hz)', dt, 1 / math.max(dt, 0.0001)))
  end
end

----------------------------------------------------------------------
-- HUD RENDERING
----------------------------------------------------------------------
local hudAnimTimer = 0
local pursuitPulse = 0

function script.drawUI()
  if not initialized or #cops == 0 then
    -- No cops found warning
    ui.pushFont(ui.Font.Title)
    local ws = ui.windowSize()
    ui.setCursor(vec2(ws.x / 2 - 200, 60))
    ui.textColored('No police cars detected in session', rgbm(1, 0.3, 0.3, 1))
    ui.popFont()
    return
  end

  hudAnimTimer = hudAnimTimer + ui.deltaTime()
  pursuitPulse = (math.sin(hudAnimTimer * 6) + 1) * 0.5

  -- Surface script errors so failures are never silent
  if lastError then
    ui.pushFont(ui.Font.Small)
    ui.setCursor(vec2(20, 100))
    ui.textColored('SCRIPT ERROR: ' .. lastError, rgbm(1, 0.3, 0.3, 1))
    ui.popFont()
  end

  local ws = ui.windowSize()
  local player = ac.getCar(playerIndex)
  if not player then return end

  local playerSpeed = player.speedKmh

  -- =====================================================
  -- TOP-CENTER: PURSUIT STATUS BAR
  -- =====================================================
  local barW = 320
  local barH = 44
  local barX = (ws.x - barW) / 2
  local barY = 16

  -- Background with glassmorphism
  ui.drawRectFilled(
    vec2(barX - 2, barY - 2),
    vec2(barX + barW + 2, barY + barH + 2),
    rgbm(0, 0, 0, 0.7),
    8
  )

  -- Phase-colored accent bar at top
  local phaseColor = PHASE_COLORS[phase] or rgbm(1, 1, 1, 1)
  if phase == PHASE_PURSUIT then
    -- Pulsing red during pursuit
    phaseColor = lerpColor(rgbm(0.8, 0, 0, 1), rgbm(1, 0.2, 0.2, 1), pursuitPulse)
  end

  ui.drawRectFilled(
    vec2(barX, barY),
    vec2(barX + barW, barY + 3),
    phaseColor,
    0
  )

  -- Phase name
  ui.pushFont(ui.Font.Title)
  local phaseName = PHASE_NAMES[phase] or 'UNKNOWN'
  ui.setCursor(vec2(barX + 12, barY + 8))
  ui.textColored(phaseName, phaseColor)
  ui.popFont()

  -- Heat stars
  if heatLevel > 0 then
    ui.pushFont(ui.Font.Main)
    local stars = string.rep('★', heatLevel) .. string.rep('☆', settings.HEAT_LEVELS - heatLevel)
    local starsW = ui.measureText(stars)
    ui.setCursor(vec2(barX + barW - starsW.x - 12, barY + 12))
    ui.textColored(stars, rgbm(1, 0.85, 0, 1))
    ui.popFont()
  end

  -- =====================================================
  -- TOP-LEFT: SPEED DISPLAY
  -- =====================================================
  local spdX = 20
  local spdY = 20
  local spdW = 140
  local spdH = 70

  -- Background
  ui.drawRectFilled(
    vec2(spdX, spdY),
    vec2(spdX + spdW, spdY + spdH),
    rgbm(0, 0, 0, 0.6),
    6
  )

  -- Speed number
  local speedStr = tostring(math.floor(playerSpeed))
  local speedColor
  if playerSpeed < settings.SPEED_THRESHOLD * 0.8 then
    speedColor = rgbm(0.3, 1, 0.3, 1)   -- green — safe
  elseif playerSpeed < settings.SPEED_THRESHOLD then
    speedColor = rgbm(1, 0.85, 0, 1)     -- yellow — approaching limit
  else
    speedColor = rgbm(1, 0.15, 0.15, 1)  -- red — over limit
  end

  ui.pushFont(ui.Font.Title)
  ui.setCursor(vec2(spdX + 10, spdY + 5))
  ui.textColored(speedStr, speedColor)
  ui.popFont()

  ui.pushFont(ui.Font.Small)
  ui.setCursor(vec2(spdX + 10, spdY + 48))
  ui.textColored('KM/H', rgbm(0.6, 0.6, 0.6, 1))

  -- Speed limit indicator
  ui.setCursor(vec2(spdX + 80, spdY + 48))
  ui.textColored('LIMIT: ' .. settings.SPEED_THRESHOLD, rgbm(0.5, 0.5, 0.5, 1))
  ui.popFont()

  -- =====================================================
  -- TOP-RIGHT: COP PROXIMITY RADAR
  -- =====================================================
  if phase == PHASE_PURSUIT or phase == PHASE_DETECTED then
    local radarX = ws.x - 180
    local radarY = 20
    local radarW = 160
    local radarH = 55

    ui.drawRectFilled(
      vec2(radarX, radarY),
      vec2(radarX + radarW, radarY + radarH),
      rgbm(0, 0, 0, 0.6),
      6
    )

    -- Nearest cop distance
    ui.pushFont(ui.Font.Small)
    ui.setCursor(vec2(radarX + 8, radarY + 6))
    ui.textColored('NEAREST COP', rgbm(0.6, 0.6, 0.6, 1))
    ui.popFont()

    ui.pushFont(ui.Font.Main)
    local distStr
    if closestCopDist < 1000 then
      distStr = math.floor(closestCopDist) .. 'm'
    else
      distStr = string.format('%.1fkm', closestCopDist / 1000)
    end
    ui.setCursor(vec2(radarX + 8, radarY + 24))
    local distColor = closestCopDist < 50 and rgbm(1, 0, 0, 1)
                   or closestCopDist < 150 and rgbm(1, 0.5, 0, 1)
                   or rgbm(0.3, 1, 0.3, 1)
    ui.textColored(distStr, distColor)
    ui.popFont()

    -- Escape progress bar
    if escapeTimer > 0 then
      local escProg = escapeTimer / settings.ESCAPE_TIME
      local escBarY = radarY + radarH - 8
      ui.drawRectFilled(
        vec2(radarX + 4, escBarY),
        vec2(radarX + radarW - 4, escBarY + 4),
        rgbm(0.2, 0.2, 0.2, 0.8),
        2
      )
      ui.drawRectFilled(
        vec2(radarX + 4, escBarY),
        vec2(radarX + 4 + (radarW - 8) * escProg, escBarY + 4),
        rgbm(0.2, 0.6, 1, 1),
        2
      )
    end
  end

  -- =====================================================
  -- BUSTED OVERLAY
  -- =====================================================
  if phase == PHASE_BUSTED then
    -- Dark overlay
    local overlayAlpha = math.min(0.7, bustedTimer * 0.35)
    ui.drawRectFilled(vec2(0, 0), ws, rgbm(0, 0, 0, overlayAlpha))

    -- Flashing red/blue bars at top and bottom
    local flashPhase = math.floor(bustedFlashTimer * 4) % 2
    local barColor1 = flashPhase == 0 and rgbm(1, 0, 0, 0.6) or rgbm(0, 0.2, 1, 0.6)
    local barColor2 = flashPhase == 0 and rgbm(0, 0.2, 1, 0.6) or rgbm(1, 0, 0, 0.6)

    ui.drawRectFilled(vec2(0, 0), vec2(ws.x / 2, 6), barColor1)
    ui.drawRectFilled(vec2(ws.x / 2, 0), vec2(ws.x, 6), barColor2)
    ui.drawRectFilled(vec2(0, ws.y - 6), vec2(ws.x / 2, ws.y), barColor2)
    ui.drawRectFilled(vec2(ws.x / 2, ws.y - 6), vec2(ws.x, ws.y), barColor1)

    -- BUSTED text
    if bustedTimer > 0.5 then
      ui.pushFont(ui.Font.Title)
      local bustedText = 'B U S T E D'
      local textSize = ui.measureText(bustedText)
      local textX = (ws.x - textSize.x * 2.5) / 2
      local textY = ws.y * 0.38

      -- Text shadow
      ui.setCursor(vec2(textX + 3, textY + 3))
      ui.textColored(bustedText, rgbm(0, 0, 0, 0.8))

      -- Main text
      ui.setCursor(vec2(textX, textY))
      local textAlpha = math.min(1, (bustedTimer - 0.5) * 2)
      ui.textColored(bustedText, rgbm(1, 0.1, 0.1, textAlpha))
      ui.popFont()

      -- Sub-text
      if bustedTimer > 1.5 then
        ui.pushFont(ui.Font.Main)
        local subText = 'Heat Level: ' .. string.rep('★', heatLevel)
        local subSize = ui.measureText(subText)
        ui.setCursor(vec2((ws.x - subSize.x) / 2, textY + 80))
        ui.textColored(subText, rgbm(1, 0.85, 0, math.min(1, (bustedTimer - 1.5) * 2)))
        ui.popFont()
      end

      -- Countdown
      if bustedTimer > 3 then
        ui.pushFont(ui.Font.Small)
        local remaining = math.max(0, math.ceil(6 - bustedTimer))
        local countdownText = 'Respawning in ' .. remaining .. '...'
        local cdSize = ui.measureText(countdownText)
        ui.setCursor(vec2((ws.x - cdSize.x) / 2, textY + 120))
        ui.textColored(countdownText, rgbm(0.7, 0.7, 0.7, 1))
        ui.popFont()
      end
    end
  end

  -- =====================================================
  -- ESCAPED OVERLAY
  -- =====================================================
  if phase == PHASE_ESCAPED then
    ui.pushFont(ui.Font.Title)
    local escText = 'E S C A P E D'
    local escSize = ui.measureText(escText)
    local escX = (ws.x - escSize.x * 2) / 2
    local escY = ws.y * 0.38
    local escAlpha = 1 - math.max(0, (escapedDisplayTimer - 2.5) / 1.5)

    ui.setCursor(vec2(escX, escY))
    ui.textColored(escText, rgbm(0.2, 0.7, 1, escAlpha))
    ui.popFont()

    -- Cool down message
    if escapedDisplayTimer > 1 then
      ui.pushFont(ui.Font.Main)
      local cdText = 'Heat cleared. Drive safe... or don\'t.'
      local cdSize = ui.measureText(cdText)
      ui.setCursor(vec2((ws.x - cdSize.x) / 2, escY + 60))
      ui.textColored(cdText, rgbm(0.6, 0.6, 0.6, escAlpha))
      ui.popFont()
    end
  end

  -- =====================================================
  -- DEBUG OVERLAY
  -- =====================================================
  if settings.DEBUG_MODE then
    ui.pushFont(ui.Font.Small)
    local dbgX = 20
    local dbgY = ws.y - 200

    ui.drawRectFilled(
      vec2(dbgX, dbgY),
      vec2(dbgX + 280, dbgY + 180),
      rgbm(0, 0, 0, 0.7),
      4
    )

    local function dbgLine(y, text, color)
      ui.setCursor(vec2(dbgX + 8, y))
      ui.textColored(text, color or rgbm(0.8, 0.8, 0.8, 1))
    end

    dbgLine(dbgY + 4,  '--- POLICE PURSUIT DEBUG ---', rgbm(1, 0.5, 0, 1))
    dbgLine(dbgY + 20, 'Phase: ' .. (PHASE_NAMES[phase] or '?'), PHASE_COLORS[phase])
    dbgLine(dbgY + 36, 'Heat: ' .. heatLevel .. '/' .. settings.HEAT_LEVELS)
    dbgLine(dbgY + 52, 'Speed: ' .. math.floor(playerSpeed) .. ' km/h (limit: ' .. settings.SPEED_THRESHOLD .. ')')
    dbgLine(dbgY + 68, 'Cops Total: ' .. #cops)
    dbgLine(dbgY + 84, 'Closest Cop: ' .. math.floor(closestCopDist) .. 'm')
    dbgLine(dbgY + 100, 'Escape Timer: ' .. string.format('%.1f', escapeTimer) .. '/' .. settings.ESCAPE_TIME .. 's')

    local pursuingCount = 0
    for _, cop in ipairs(cops) do
      if cop.pursuing then pursuingCount = pursuingCount + 1 end
    end
    dbgLine(dbgY + 116, 'Pursuing: ' .. pursuingCount .. '/' .. #cops)

    for i, cop in ipairs(cops) do
      if i <= 3 then
        local copCar = ac.getCar(cop.index)
        local copSpeed = copCar and math.floor(copCar.speedKmh) or 0
        local status = cop.pursuing and 'PURSUIT' or 'PATROL'
        dbgLine(dbgY + 116 + i * 16,
          'Cop#' .. cop.index .. ': ' .. status .. ' | ' .. math.floor(cop.distance) .. 'm | ' .. copSpeed .. 'km/h',
          cop.pursuing and rgbm(1, 0.3, 0.3, 1) or rgbm(0.5, 0.8, 0.5, 1))
      end
    end

    ui.popFont()
  end
end

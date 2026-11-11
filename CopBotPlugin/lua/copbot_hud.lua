-- CopBot HUD: speed cluster, wanted stars, the pursuit meter, police voices and the busted / escaped screens

local PHASE_CHASE, PHASE_BUSTED, PHASE_ESCAPED = 1, 2, 3
local ALERT_ROADBLOCK, ALERT_HEAD_ON, ALERT_UNIT_DOWN = 1, 2, 3

local state = { phase = 0, stars = 0, cops = 0, down = 0, bust = 0, escape = 0, nearest = 0, alertId = 0 }
local phaseTime = 0
local clock = 0

-- Real recordings, sent by the server so everyone hears them
local AUDIO_URL = 'http://' .. ac.getServerIP() .. ':' .. ac.getServerPortHTTP() .. '/copbot/audio/'
local LOUDSPEAKER = {
  { id = 'this_is_the_police', text = 'THIS IS THE POLICE!' },
  { id = 'pull_over', text = 'PULL OVER!' },
  { id = 'pull_over_side', text = 'PULL OVER TO THE SIDE!' },
  { id = 'pull_over_now', text = 'PULL OVER NOW!' },
  { id = 'move_vehicle_over', text = 'MOVE YOUR VEHICLE OVER!' },
  { id = 'pull_it_over', text = 'PULL IT OVER!' },
  { id = 'freeze', text = "FREEZE! THIS IS THE POLICE! DON'T MOVE!" },
}
local RADIO_START = { id = 'radio_pull_him_over', text = "I GOT A DRUNK DRIVER HERE IN FRONT OF ME, I'M GONNA PULL HIM OVER", radio = true }
local RADIO_ALL_UNITS = { id = 'radio_all_units', text = 'ATTENTION ALL UNITS, THE AIR IS BEING HELD FOR EMERGENCY TRAFFIC ONLY', radio = true }
local RADIO_ON_THAT_CAR = { id = 'radio_on_that_car', text = "I'M LOOKING AT THAT VEHICLE NOW", radio = true }
local RADIO_ON_THE_ROAD = { id = 'radio_on_the_road', text = "COPY, I'M ON THE ROAD HERE", radio = true }
local RADIO_BEEP = { id = 'radio_beep', radio = true }
local SURRENDER = {
  { id = 'hands_on_head', text = 'PUT YOUR HANDS ON YOUR HEAD AND GET DOWN ON THE GROUND!' },
  { id = 'see_those_hands', text = "LET'S SEE THOSE HANDS!" },
}
local ARRESTED = {
  { id = 'under_arrest', text = 'YOU ARE UNDER ARREST!' },
  { id = 'placed_under_arrest', text = 'YOU ARE GOING TO BE PLACED UNDER ARREST.' },
}

local WHITE = rgbm(1, 1, 1, 1)
local MUTED = rgbm(1, 1, 1, 0.55)
local PANEL = rgbm(0.04, 0.05, 0.07, 0.62)
local TRACK = rgbm(1, 1, 1, 0.14)
local RED = rgbm(1, 0.16, 0.2, 1)
local BLUE = rgbm(0.2, 0.45, 1, 1)
local AMBER = rgbm(1, 0.72, 0.15, 1)
local GREEN = rgbm(0.3, 0.9, 0.55, 1)

local ALERTS = {
  [ALERT_ROADBLOCK] = { text = 'ROADBLOCK AHEAD', r = 1, g = 0.72, b = 0.15 },
  [ALERT_HEAD_ON] = { text = 'UNIT COMING HEAD ON', r = 1, g = 0.16, b = 0.2 },
  [ALERT_UNIT_DOWN] = { text = 'UNIT DOWN', r = 0.3, g = 0.9, b = 0.55 },
}

local players = {}
local shout = nil
local shoutTime = 0
local nextShout = 0
local nextLine = 1
local lastStars = 0
local alert = nil
local alertTime = 0
local rotor = nil
local rotorPlaying = false
local rotorVolume = 0

local ROTOR_FROM_STARS = 4
local ROTOR_VOLUME = 0.3

local function say(line)
  if line.text then
    shout = line.text
    shoutTime = 0
  end

  -- Loudspeakers fade with distance, the radio is in your ear
  local volume = line.radio and 0.9 or math.clamp(1.15 - state.nearest / 180, 0.35, 1)
  local player = players[line.id]
  if player then
    player:setVolume(volume)
    player:setCurrentTime(0)
    player:play()
  else
    player = ui.MediaPlayer(AUDIO_URL .. line.id .. '.mp3')
    player:setVolume(volume)
    player:setAutoPlay(true)
    players[line.id] = player
  end
end

ac.OnlineEvent({
  ac.StructItem.key("copBotState"),
  phase = ac.StructItem.byte(),
  stars = ac.StructItem.byte(),
  cops = ac.StructItem.byte(),
  down = ac.StructItem.byte(),
  bust = ac.StructItem.byte(),
  escape = ac.StructItem.byte(),
  alert = ac.StructItem.byte(),
  alertId = ac.StructItem.byte(),
  nearest = ac.StructItem.uint16(),
}, function(sender, message)
  if sender ~= nil then return end
  if message.phase ~= state.phase then
    phaseTime = 0
    if message.phase == PHASE_CHASE then
      say(RADIO_START)
      nextShout = clock + 6
    elseif message.phase == PHASE_BUSTED then
      say(ARRESTED[math.random(#ARRESTED)])
    end
  end
  if message.alertId ~= state.alertId then
    alert = ALERTS[message.alert]
    alertTime = 0
    if message.alert == ALERT_ROADBLOCK then
      say(RADIO_ALL_UNITS)
      nextShout = clock + 7
    elseif message.alert == ALERT_HEAD_ON then
      say(RADIO_BEEP)
    elseif message.alert == ALERT_UNIT_DOWN then
      say(RADIO_ON_THE_ROAD)
      nextShout = math.max(nextShout, clock + 3)
    end
  end
  state.phase = message.phase
  state.stars = message.stars
  state.cops = message.cops
  state.down = message.down
  state.bust = message.bust
  state.escape = message.escape
  state.alertId = message.alertId
  state.nearest = message.nearest
end)

local function textCentered(text, size, centerX, y, color)
  local width = ui.measureDWriteText(text, size).x
  ui.dwriteDrawText(text, size, vec2(centerX - width / 2, y), color)
end

local function textRight(text, size, rightX, y, color)
  local width = ui.measureDWriteText(text, size).x
  ui.dwriteDrawText(text, size, vec2(rightX - width, y), color)
end

local function bar(x, y, w, h, fraction, color)
  ui.drawRectFilled(vec2(x, y), vec2(x + w, y + h), TRACK, h / 2)
  if fraction > 0 then
    ui.drawRectFilled(vec2(x, y), vec2(x + w * math.min(fraction, 1), y + h), color, h / 2)
  end
end

local function drawSpeed(screen, car)
  local w, h = 230, 104
  local x, y = screen.x - w - 28, screen.y - h - 28
  ui.drawRectFilled(vec2(x, y), vec2(x + w, y + h), PANEL, 12)

  textRight(string.format('%d', math.floor(car.speedKmh + 0.5)), 52, x + w - 74, y + 6, WHITE)
  ui.dwriteDrawText('km/h', 13, vec2(x + w - 66, y + 42), MUTED)

  local gear = car.gear == -1 and 'R' or car.gear == 0 and 'N' or tostring(car.gear)
  ui.dwriteDrawText(gear, 34, vec2(x + 18, y + 18), WHITE)

  local limit = car.rpmLimiter > 0 and car.rpmLimiter or 7000
  local fraction = car.rpm / limit
  bar(x + 16, y + h - 26, w - 32, 7, fraction, fraction > 0.9 and RED or fraction > 0.75 and AMBER or WHITE)
end

local function drawWanted(screen)
  local w, h = 300, 72
  local x, y = (screen.x - w) / 2, 22
  local centerX = screen.x / 2
  ui.drawRectFilled(vec2(x, y), vec2(x + w, y + h), PANEL, 12)

  -- Strip along the top alternates like a light bar
  local flash = math.floor(clock * 4) % 2 == 0
  ui.drawRectFilled(vec2(x + 12, y), vec2(centerX, y + 3), flash and RED or BLUE)
  ui.drawRectFilled(vec2(centerX, y), vec2(x + w - 12, y + 3), flash and BLUE or RED)

  local gap = 34
  local startX = centerX - gap * 2
  for i = 1, 5 do
    local filled = i <= state.stars
    textCentered(filled and '★' or '☆', 24, startX + (i - 1) * gap, y + 10, filled and AMBER or TRACK)
  end

  local units = string.format('%d %s', state.cops, state.cops == 1 and 'unit' or 'units')
  if state.down > 0 then units = units .. string.format('  ·  %d down', state.down) end
  textCentered(units .. string.format('  ·  %d m', state.nearest), 14, centerX, y + 44, MUTED)
end

-- The two ways a chase can end, either side of the middle: arrested on the left, away on the right
local function drawPursuitMeter(screen)
  local half, h = 190, 8
  local centerX, y = screen.x / 2, screen.y - 48

  local bustFlash = state.bust > 60 and math.floor(clock * 8) % 2 == 0
  ui.dwriteDrawText('BUST', 13, vec2(centerX - half - 46, y - 5), state.bust > 0 and (bustFlash and WHITE or RED) or MUTED)
  ui.dwriteDrawText('EVADE', 13, vec2(centerX + half + 10, y - 5), state.escape > 0 and GREEN or MUTED)

  ui.drawRectFilled(vec2(centerX - half, y), vec2(centerX + half, y + h), TRACK, h / 2)
  if state.bust > 0 then
    ui.drawRectFilled(vec2(centerX - half * state.bust / 100, y), vec2(centerX, y + h), RED, h / 2)
  end
  if state.escape > 0 then
    ui.drawRectFilled(vec2(centerX, y), vec2(centerX + half * state.escape / 100, y + h), GREEN, h / 2)
  end
  ui.drawRectFilled(vec2(centerX - 1, y - 3), vec2(centerX + 1, y + h + 3), WHITE)
end

local function drawAlert(screen)
  if not alert or alertTime > 3.5 then return end

  -- Flashes to get attention, fades out at the end
  local fade = math.clamp((3.5 - alertTime) * 3, 0, 1)
  local pulse = 0.65 + 0.35 * math.abs(math.sin(alertTime * 7))
  textCentered(alert.text, 30, screen.x / 2, 102, rgbm(alert.r, alert.g, alert.b, fade * pulse))
end

local function drawBusted(screen)
  local alpha = math.min(phaseTime * 2.5, 1)
  ui.drawRectFilled(vec2(0, 0), screen, rgbm(0, 0, 0, 0.72 * alpha))

  local flash = math.floor(clock * 5) % 2 == 0
  local left, right = flash and RED or BLUE, flash and BLUE or RED
  ui.drawRectFilled(vec2(0, 0), vec2(screen.x / 2, 8), left)
  ui.drawRectFilled(vec2(screen.x / 2, 0), vec2(screen.x, 8), right)
  ui.drawRectFilled(vec2(0, screen.y - 8), vec2(screen.x / 2, screen.y), right)
  ui.drawRectFilled(vec2(screen.x / 2, screen.y - 8), screen, left)

  local centerX, y = screen.x / 2, screen.y * 0.36
  textCentered('BUSTED', 92, centerX, y, rgbm(1, 0.16, 0.2, alpha))
  textCentered(string.rep('★', state.stars), 28, centerX, y + 112, rgbm(1, 0.72, 0.15, alpha))
  textCentered('Back to the pits', 18, centerX, y + 156, rgbm(1, 1, 1, 0.7 * alpha))
end

local function drawEscaped(screen)
  local alpha = math.min(phaseTime * 3, 1) * math.clamp((3 - phaseTime) * 2, 0, 1)
  local centerX, y = screen.x / 2, 44
  textCentered('ESCAPED', 44, centerX, y, rgbm(0.3, 0.9, 0.55, alpha))
  textCentered('Heat is off', 15, centerX, y + 54, rgbm(1, 1, 1, 0.65 * alpha))
end

-- Air support: there is no helicopter to see, but it can be heard overhead once the chase is hot enough
local function updateRotor(dt)
  local wanted = state.phase == PHASE_CHASE and state.stars >= ROTOR_FROM_STARS and ROTOR_VOLUME or 0
  if wanted == 0 and rotorVolume == 0 then return end

  if not rotor then
    rotor = ui.MediaPlayer(AUDIO_URL .. 'rotor.mp3')
    rotor:setLooping(true)
    rotor:setVolume(0)
    rotor:setAutoPlay(true)
    rotorPlaying = true
  elseif not rotorPlaying and wanted > 0 then
    rotor:play()
    rotorPlaying = true
  end

  rotorVolume = wanted > rotorVolume and math.min(wanted, rotorVolume + dt * 0.08) or math.max(wanted, rotorVolume - dt * 0.2)
  rotor:setVolume(rotorVolume)
  if rotorVolume == 0 then
    rotor:pause()
    rotorPlaying = false
  end
end

function script.update(dt)
  clock = clock + dt
  phaseTime = phaseTime + dt
  shoutTime = shoutTime + dt
  alertTime = alertTime + dt
  updateRotor(dt)

  if state.phase == PHASE_CHASE then
    if state.stars > lastStars and state.stars == 2 then
      say(RADIO_ON_THAT_CAR)
      nextShout = clock + 5
    elseif state.stars > lastStars and state.stars == 3 then
      say(RADIO_ALL_UNITS)
      nextShout = clock + 7
    elseif state.bust > 0 and clock >= nextShout then
      say(SURRENDER[math.random(#SURRENDER)])
      nextShout = clock + 6
    elseif clock >= nextShout and state.cops > 0 and state.nearest < 150 then
      say(LOUDSPEAKER[nextLine])
      nextLine = nextLine % #LOUDSPEAKER + 1
      nextShout = clock + 7 + math.random() * 5
    end
    lastStars = state.stars
  else
    lastStars = 0
    nextLine = 1
    alert = nil
  end
end

function script.drawUI()
  local screen = ui.windowSize()
  local car = ac.getCar(0)

  if car and state.phase ~= PHASE_BUSTED then drawSpeed(screen, car) end

  if state.phase == PHASE_CHASE then
    drawWanted(screen)
    drawPursuitMeter(screen)
    drawAlert(screen)
    if shout and shoutTime < 3.5 then
      local alpha = math.min(shoutTime * 5, 1) * math.clamp((3.5 - shoutTime) * 3, 0, 1)
      textCentered(shout, 22, screen.x / 2, 142, rgbm(1, 1, 1, alpha))
    end
  elseif state.phase == PHASE_BUSTED then
    drawBusted(screen)
    if shout and shoutTime < 3.5 then
      local alpha = math.clamp((3.5 - shoutTime) * 3, 0, 1)
      textCentered(shout, 22, screen.x / 2, screen.y * 0.36 - 44, rgbm(1, 1, 1, alpha))
    end
  elseif state.phase == PHASE_ESCAPED then
    drawEscaped(screen)
  end
end

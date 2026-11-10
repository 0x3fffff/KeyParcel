local Accepted, Noop = 1, 2
local F13, F24 = 0xffca, 0xffd5
local root = os.getenv("LOCALAPPDATA")
if root then root = root .. "/KeyParcel/" end

local function request()
  local f = root and io.open(root .. "rime-request.txt", "rb")
  if not f then return nil end
  local value = f:read(33)
  f:close()
  if value and #value == 32 and value:match("^[a-f0-9]+$") then return value end
end

local function acknowledge(env)
  local f = io.open(root .. "rime-reply.txt", "wb")
  if not f then return false end
  f:write(env.nonce, ":", tostring(env.sequence))
  return f:close()
end

local function process(key, env)
  local code = key.keycode
  if code < F13 or code > F24 then
    if not key:release() then env.nonce = nil end
    return Noop
  end

  local nonce = request()
  if not nonce then env.nonce = nil; return Noop end
  if key:release() then return Accepted end
  if key:ctrl() or key:alt() or key:shift() or key:super() then return Noop end

  if code == F24 then
    env.nonce = nil
    if not env.engine.context:is_composing() then
      env.nonce, env.sequence, env.digits, env.scalar = nonce, 0, 0, 0
      if not acknowledge(env) then env.nonce = nil end
    end
    return Accepted
  end

  if env.nonce ~= nonce then return Accepted end
  env.digits, env.scalar = env.digits + 1, env.scalar * 11 + code - F13
  if env.digits == 6 then
    local scalar = env.scalar
    env.digits, env.scalar = 0, 0
    if scalar > 0x10ffff or (scalar >= 0xd800 and scalar <= 0xdfff) or
        (scalar < 0x20 and scalar ~= 0x0a) or (scalar >= 0x7f and scalar <= 0x9f) then
      env.nonce = nil
      return Accepted
    end
    env.engine:commit_text(utf8.char(scalar))
    env.sequence = env.sequence + 1
    if not acknowledge(env) then env.nonce = nil end
  end
  return Accepted
end

return { func = process }

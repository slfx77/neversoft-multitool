-- Capture later Vicarious Visions GBA carts while they idle into their attract
-- sequence.  BizHawk 2.6.3 / mGBA core / Lua 5.1.
--
-- The cartridge game code chooses the output folder, so the same script can be
-- run against every ROM without editing it.  Screenshots are frequent enough to
-- catch menu and gameplay transitions; full GBA video state is dumped beside
-- the first screenshot from each interval for static/runtime comparison.

-- Preserve retained captures when no environment override is supplied. For
-- repeated experiments, set NM_GBA_CAPTURE_ROOT to a unique timestamped root.
local ROOT = os.getenv("NM_GBA_CAPTURE_ROOT") or "TestOutput/gba-runtime-capture-rerun"
ROOT = string.gsub(ROOT, "\\", "/") .. "/"
local LAST_FRAME = 12000
local CAPTURE_EVERY = 300
local STATE_EVERY = 1200
local autoplay = os.getenv("NM_GBA_CAPTURE_AUTOPLAY") == "1"

local function bytes_at(address, length)
  local values = memory.read_bytes_as_array(address, length, "System Bus")
  if values[0] ~= nil then
    local one_based = {}
    for i = 0, length - 1 do one_based[i + 1] = values[i] end
    return one_based
  end
  return values
end

local code_bytes = bytes_at(0x080000AC, 4)
local code = string.char(code_bytes[1], code_bytes[2], code_bytes[3], code_bytes[4])
local out = ROOT .. code .. (autoplay and "-auto/" or "/")
os.execute('mkdir "' .. out .. '" 2>NUL')

local function dump(path, address, length)
  local values = bytes_at(address, length)
  local file = assert(io.open(path, "wb"))
  local first = 1
  while first <= length do
    local last = math.min(first + 4095, length)
    file:write(string.char(unpack(values, first, last)))
    first = last + 1
  end
  file:close()
end

pcall(function() client.speedmode(1600) end)
pcall(function() client.unpause() end)

local press_frames = {}
for frame = 1800, LAST_FRAME - 300, 300 do press_frames[frame] = true end
local hold_confirm = 0
while emu.framecount() <= LAST_FRAME do
  if autoplay and hold_confirm > 0 then
    -- Some entries bind title-screen confirmation to Start and others to A.
    -- Holding both for a few frames is harmless in the menus and lets the same
    -- capture script traverse every cartridge in the series.
    joypad.set({ A = true, Start = true })
    hold_confirm = hold_confirm - 1
  end
  emu.frameadvance()
  local frame = emu.framecount()
  if autoplay and press_frames[frame] then hold_confirm = 5 end
  if frame % CAPTURE_EVERY == 0 then
    local stem = out .. string.format("frame_%06d", frame)
    client.screenshot(stem .. ".png")
    if frame % STATE_EVERY == 0 then
      dump(stem .. "_vram.bin", 0x06000000, 0x18000)
      dump(stem .. "_palette.bin", 0x05000000, 0x400)
      dump(stem .. "_oam.bin", 0x07000000, 0x400)
      dump(stem .. "_iwram.bin", 0x03000000, 0x8000)
      dump(stem .. "_ewram.bin", 0x02000000, 0x40000)
    end
  end
end

client.exit()

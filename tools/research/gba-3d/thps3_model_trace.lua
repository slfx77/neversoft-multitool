-- Trace THPS3 GBA's live software-rendered skater path in BizHawk 2.6.3.
--
-- The skater is OAM object 13 in the retained attract-demo captures: a 64x64
-- 8bpp object at tile 86.  This bounded probe records the live transfer into
-- that tile.  BizHawk 2.6.3 does not populate the write callback's address/value
-- arguments for mGBA DMA, so the register predicate is intentionally load-bearing:
-- the transfer routine has destination in R0 and source in R1.  It does not
-- assume that the large EWRAM pointer lattices are model frames (they vary with
-- the level and point into the level-art banks).

local out = os.getenv("NM_GBA_THPS3_MODEL_TRACE") or
  "TestOutput/gba-thps3-model-trace-rerun.txt"
out = string.gsub(out, "\\", "/")
local file = assert(io.open(out, "w"))
local LAST_FRAME = tonumber(os.getenv("NM_GBA_THPS3_MODEL_TRACE_LAST_FRAME") or "6700")
local FIRST_TRACE_FRAME = tonumber(os.getenv("NM_GBA_THPS3_MODEL_TRACE_FIRST_FRAME") or "6400")
local hit_count = 0
local MAX_HITS = 128

local function hex(value)
  if type(value) ~= "number" then return tostring(value) end
  if value < 0 then value = value + 4294967296 end
  return string.format("%08X", value)
end

local function cpu_state()
  local ok, registers = pcall(function() return emu.getregisters() end)
  if not ok or type(registers) ~= "table" then
    return "registers-unavailable=" .. tostring(registers)
  end
  local wanted = {
    "PC", "R15", "LR", "R14", "SP", "R13",
    "R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7",
    "R8", "R9", "R10", "R11", "R12"
  }
  local fields = {}
  for _, name in ipairs(wanted) do
    if registers[name] ~= nil then
      table.insert(fields, name .. "=" .. hex(registers[name]))
    end
  end
  return table.concat(fields, " ")
end

local function trace(label, address, value, flags)
  local frame = emu.framecount()
  if frame < FIRST_TRACE_FRAME or hit_count >= MAX_HITS then return end
  hit_count = hit_count + 1
  file:write(string.format(
    "frame=%d hit=%d label=%s addr=%s value=%s flags=%s %s\n",
    frame, hit_count, label, hex(address), hex(value), tostring(flags), cpu_state()))
  file:flush()
end

local function watch_upload(address)
  local ok, result = pcall(function()
    return event.onmemorywrite(function(a, v, f)
      local ok_registers, registers = pcall(function() return emu.getregisters() end)
      if not ok_registers or type(registers) ~= "table" then return end
      if registers.R0 ~= 0x06010AC0 then return end
      if type(registers.R1) ~= "number" or
        registers.R1 < 0x02000000 or registers.R1 >= 0x02040000 then return end
      trace("skater-upload source=" .. hex(registers.R1), a, v, f)
    end, address, "thps3-model-upload", "System Bus")
  end)
  file:write(string.format("watch-write skater-upload %s ok=%s result=%s\n",
    hex(address), tostring(ok), tostring(result)))
end

watch_upload(0x06010AC0)

pcall(function() client.speedmode(1600) end)
pcall(function() client.unpause() end)

local press_frames = {}
for frame = 1800, LAST_FRAME - 300, 300 do press_frames[frame] = true end
local hold_confirm = 0
while emu.framecount() <= LAST_FRAME do
  if hold_confirm > 0 then
    joypad.set({ A = true, Start = true })
    hold_confirm = hold_confirm - 1
  end
  emu.frameadvance()
  local frame = emu.framecount()
  if press_frames[frame] then hold_confirm = 5 end
end

file:write(string.format("complete frame=%d hits=%d\n", emu.framecount(), hit_count))
file:close()
client.exit()

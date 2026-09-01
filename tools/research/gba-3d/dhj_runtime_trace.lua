-- Trace the Downhill Jam rider (and legacy course anchors) in BizHawk 2.6.3.
--
-- Run with:
--   EmuHawk.exe "Tony Hawk's Downhill Jam (USA).gba" --lua=dhj_runtime_trace.lua
--
-- The script drives the title screen in the same way as later_idle_capture.lua,
-- then records the CPU state when the live rider descriptor and its ROM banks
-- are read.  Callback support differs between BizHawk releases, so failed
-- watchpoint registrations are written to the log rather than aborting the run.

-- Keep the retained gba-dhj-runtime-trace.txt evidence safe on an unconfigured
-- rerun. Set NM_GBA_DHJ_TRACE to a unique timestamped path for each experiment.
local out = os.getenv("NM_GBA_DHJ_TRACE") or "TestOutput/gba-dhj-runtime-trace-rerun.txt"
out = string.gsub(out, "\\", "/")
local file = assert(io.open(out, "w"))
local LAST_FRAME = tonumber(os.getenv("NM_GBA_DHJ_TRACE_LAST_FRAME") or "5200")
local mode = os.getenv("NM_GBA_DHJ_TRACE_MODE") or "rider"
local hit_count = 0
local MAX_HITS = 256

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
  local wanted = { "PC", "R15", "LR", "R14", "SP", "R13", "R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8", "R9", "R10", "R11", "R12" }
  local fields = {}
  for _, name in ipairs(wanted) do
    if registers[name] ~= nil then
      table.insert(fields, name .. "=" .. hex(registers[name]))
    end
  end
  if #fields == 0 then
    for name, value in pairs(registers) do
      table.insert(fields, tostring(name) .. "=" .. hex(value))
    end
  end
  return table.concat(fields, " ")
end

local function trace(label, address, value, flags)
  if hit_count >= MAX_HITS then return end
  hit_count = hit_count + 1
  file:write(string.format("frame=%d hit=%d label=%s addr=%s value=%s flags=%s %s\n",
    emu.framecount(), hit_count, label, hex(address), hex(value), tostring(flags), cpu_state()))
  file:flush()
end

local function watch_read(label, address)
  local ok, result = pcall(function()
    return event.onmemoryread(function(a, v, f) trace(label, a, v, f) end,
      address, "dhj-" .. label, "System Bus")
  end)
  file:write(string.format("watch-read %s %s ok=%s result=%s\n",
    label, hex(address), tostring(ok), tostring(result)))
end

if mode == "course" then
  -- Legacy reproduction mode retained for the original discovery trace. The
  -- source format is now closed by GbaDhjCourse: these addresses are the first
  -- course's collision pool, first 0x30-byte chunk record, and first road edge.
  -- Use gba-dhj-level for extraction; this watch is not a current parser path.
  for delta = 0, 3 do
    watch_read("scene-a+" .. delta, 0x089E20D8 + delta)
    watch_read("scene-b+" .. delta, 0x08998BC4 + delta)
    watch_read("scene-c+" .. delta, 0x089E6824 + delta)
  end
else
  -- Live rider descriptor fields and the first bytes of the two ROM banks.
  watch_read("pose-pointer", 0x02036B90)
  watch_read("face-pointer", 0x02036BAC)
  watch_read("vertex-pointer", 0x02036BB0)
  watch_read("pose-frame", 0x08EA4520)
  watch_read("face-bank", 0x08EB7EEC)
  watch_read("vertex-bank", 0x08EB7A9C)
end

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

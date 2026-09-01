namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     A minimal ARMv4T THUMB interpreter, just large enough to <b>execute the
///     Vicarious Visions GBA collision height accessors straight out of the ROM</b> rather than
///     hand-transcribing them.
///
///     <para>The engine stores a level's collision surface as a per-cell shape byte plus
///     a material index, and the material's vtable slot 0 is a <i>function</i> that
///     computes the surface height at a sub-cell offset. THPS2 alone has 27 distinct
///     such functions; reimplementing them by hand would be 27 chances to introduce a subtly
///     wrong ramp, so this runs the real code instead. (The same approach is used
///     elsewhere in this repo to validate the N64 ERZ decoders against ROM MIPS code.)</para>
///
///     <para>Scope is deliberately narrow: only the instruction forms these accessors
///     actually execute are decoded — measured as <b>44 distinct forms / 30 mnemonics</b>
///     over all 8,520 THPS2 cells, then extended against every referenced object in
///     all 37 THPS4-through-Sk8land levels. Anything outside that set throws rather
///     than silently computing a wrong height, so a decode gap can never masquerade as
///     a flat surface. Pure arithmetic ROM/IWRAM veneers are supplied as hooks instead
///     of being executed: divide-by-cell, remainder, signed divide, and the BIOS
///     integer square root.</para>
///
///     <para>The memory model is equally narrow — ROM reads, a 32-byte THPS2 record
///     or 40-byte later height object at <see cref="RecordAddress" />, explicitly
///     named loader-published function/object-bank/scalar addresses, and a small
///     stack. Every other RAM read fails closed. There is no MMIO and no DMA; these
///     are pure computations over their arguments and authored record.</para>
/// </summary>
internal sealed class GbaThumbCpu
{
    private const uint RomBase = 0x08000000;
    private const uint StackTop = 0x03007F00;
    private const int StackBytes = 0x100;

    /// <summary>Where the current cell record or height object is mapped for the accessor.</summary>
    public const uint RecordAddress = 0x02000000;

    /// <summary>The world span of one collision cell in 20.12 fixed point (3 units).</summary>
    public const int CellSpan = 0x3000;

    // THPS2 ROM routines supplied as hooks rather than executed.
    private const uint HookDivideByCell = 0x08037950; // bx r1, r1 = *(0x03001E9C)
    private const uint HookSignedDivide = 0x08001F6C;
    private const uint HookSqrt = 0x08001088;         // BIOS SWI 0x08

    // THUG/THUG2/Sk8land use IWRAM veneers whose targets are populated at runtime.
    private const uint HookLaterDivideByCell = 0x08000344;
    private const uint HookLaterRemainder = 0x0800034C;
    private const uint HookLaterSqrt = 0x080003C0;
    private const uint HookLaterSignedDivide = 0x080003CC;

    // THPS4 predates that veneer layout.
    private const uint HookThps4DivideByCell = 0x080004A0;
    private const uint HookThps4SignedDivide = 0x080004B0;
    private const uint HookThps4Sqrt = 0x08048968;

    // THPS3's IWRAM veneer at 0x080004E0 divides by the fixed 0x3000 cell
    // span. The level query and several interpolating height objects share it.
    private const uint HookThps3DivideByCell = 0x080004E0;

    // Two THPS3 height-object types consult live gameplay state: one asks the
    // dynamic-geometry manager for a height and one tests whether a named level
    // object exists. An offline level has neither live manager, so these hooks
    // model the same false result returned by an empty runtime scene.
    private const uint HookThps3DynamicHeightQuery = 0x0804568C;
    private const uint HookThps3ObjectIdExists = 0x08010CBC;
    private const uint HookThps3ObjectByteExists = 0x08010D00;

    // A runtime function pointer the accessors load; the divide hook supplies its
    // behaviour, so only a non-null placeholder is needed here.
    private const uint DivideFnPointerSlot = 0x03001E9C;

    private const int Budget = 20000; // generous; the real accessors use far fewer steps

    private readonly uint[] _r = new uint[16];
    private readonly byte[] _stack = new byte[StackBytes];
    private readonly byte[] _record = new byte[64];
    private readonly byte[] _runtimeObjectBankPointer = new byte[4];
    private readonly byte[] _runtimeWordPointer = new byte[4];
    private readonly byte[] _runtimeByte = new byte[1];
    private uint _runtimeObjectBank;
    private uint _runtimeObjectBankAddress;
    private IReadOnlyDictionary<uint, uint>? _runtimeWords;
    private IReadOnlyDictionary<uint, byte>? _runtimeBytes;
    private bool _n, _z, _c, _v;

    /// <summary>
    ///     Executes one height accessor. <paramref name="a0" />/<paramref name="a1" /> are
    ///     the sub-cell offsets (already put through the shape transform) and
    ///     <paramref name="record" /> is the cell or 40-byte surface record mapped at
    ///     <see cref="RecordAddress" />. Optional runtime snapshots are address-exact;
    ///     supplying a value never permits another EWRAM address. Returns r0 as signed.
    /// </summary>
    public int Run(
        ReadOnlySpan<byte> rom,
        uint entry,
        int a0,
        int a1,
        ReadOnlySpan<byte> record,
        uint runtimeObjectBank = 0,
        IReadOnlyDictionary<uint, uint>? runtimeWords = null,
        uint runtimeObjectBankAddress = 0,
        IReadOnlyDictionary<uint, byte>? runtimeBytes = null)
    {
        Array.Clear(_stack);
        Array.Clear(_record);
        record[..Math.Min(record.Length, _record.Length)].CopyTo(_record);
        _runtimeObjectBank = runtimeObjectBank;
        _runtimeObjectBankAddress = runtimeObjectBankAddress;
        _runtimeWords = runtimeWords;
        _runtimeBytes = runtimeBytes;
        for (var i = 0; i < _runtimeObjectBankPointer.Length; i++)
            _runtimeObjectBankPointer[i] = (byte)(runtimeObjectBank >> (8 * i));

        Array.Clear(_r);
        _r[0] = (uint)a0;
        _r[1] = (uint)a1;
        _r[2] = RecordAddress;
        _r[13] = StackTop - 0x40;
        _r[14] = 0xFFFFFFF0; // sentinel return address
        _n = _z = _c = _v = false;

        var pc = entry;
        for (var steps = 0; ; steps++)
        {
            if (steps > Budget)
                throw new InvalidDataException("THUMB accessor exceeded its step budget");
            if (pc is 0xFFFFFFF0 or 0xFFFFFFF1)
                return (int)_r[0];
            try
            {
                pc = Step(rom, pc & ~1u);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    $"THUMB execution failed at 0x{pc & ~1u:X8} " +
                    $"(r0=0x{_r[0]:X8}, r1=0x{_r[1]:X8}, r2=0x{_r[2]:X8}, r3=0x{_r[3]:X8})",
                    exception);
            }
        }
    }

    private uint Step(ReadOnlySpan<byte> rom, uint pc)
    {
        var op = ReadHalf(rom, pc);
        var next = pc + 2;

        // Format 1/2: shift by immediate, and add/subtract register or 3-bit immediate.
        if ((op & 0xE000) == 0x0000)
        {
            var rd = op & 7;
            var rs = (op >> 3) & 7;
            if ((op & 0x1800) != 0x1800) // shifts
            {
                var imm = (op >> 6) & 0x1F;
                var kind = (op >> 11) & 3;
                _r[rd] = SetNz(Shift(kind, _r[rs], imm));
                return next;
            }

            var operand = ((op >> 10) & 1) != 0 ? (uint)((op >> 6) & 7) : _r[(op >> 6) & 7];
            _r[rd] = ((op >> 9) & 1) != 0 ? SubFlags(_r[rs], operand) : AddFlags(_r[rs], operand);
            return next;
        }

        // Format 3: move/compare/add/subtract 8-bit immediate.
        if ((op & 0xE000) == 0x2000)
        {
            var rd = (op >> 8) & 7;
            uint imm = (byte)op;
            switch ((op >> 11) & 3)
            {
                case 0: _r[rd] = SetNz(imm); break;
                case 1: SubFlags(_r[rd], imm); break;
                case 2: _r[rd] = AddFlags(_r[rd], imm); break;
                default: _r[rd] = SubFlags(_r[rd], imm); break;
            }

            return next;
        }

        // Format 4: ALU operations on low registers.
        if ((op & 0xFC00) == 0x4000)
            return AluOp(op, next);

        // Format 5: high-register operations and branch-exchange.
        if ((op & 0xFC00) == 0x4400)
        {
            var rd = (op & 7) | ((op >> 4) & 8);
            var rs = ((op >> 3) & 7) | ((op >> 3) & 8);
            switch ((op >> 8) & 3)
            {
                case 0: // add (no flags); writing PC is a branch
                {
                    var sum = unchecked(_r[rd] + _r[rs]);
                    if (rd == 15)
                        return sum;
                    _r[rd] = sum;
                    return next;
                }
                case 1: SubFlags(_r[rd], _r[rs]); return next;              // cmp
                case 2: // mov (no flags); writing PC is a branch
                    if (rd == 15)
                        return _r[rs];
                    _r[rd] = _r[rs];
                    return next;
                default: return _r[rs];                                     // bx
            }
        }

        // Format 6: PC-relative load.
        if ((op & 0xF800) == 0x4800)
        {
            var addr = ((pc + 4) & ~3u) + (uint)((byte)op * 4);
            _r[(op >> 8) & 7] = Load(rom, addr, 4, false);
            return next;
        }

        // Formats 7/8: load/store with register offset (incl. sign-extended halfword).
        if ((op & 0xF000) == 0x5000)
        {
            var rd = op & 7;
            var addr = unchecked(_r[(op >> 3) & 7] + _r[(op >> 6) & 7]);
            if ((op & 0x0200) == 0) // format 7: word / byte
            {
                var size = ((op >> 10) & 1) != 0 ? 1 : 4;
                if (((op >> 11) & 1) != 0) _r[rd] = Load(rom, addr, size, false);
                else Store(addr, size, _r[rd]);
            }
            else // format 8
            {
                switch ((op >> 10) & 3)
                {
                    case 0: Store(addr, 2, _r[rd]); break;                       // strh
                    case 1: _r[rd] = Load(rom, addr, 1, true); break;            // ldrsb
                    case 2: _r[rd] = Load(rom, addr, 2, false); break;           // ldrh
                    default: _r[rd] = Load(rom, addr, 2, true); break;           // ldrsh
                }
            }

            return next;
        }

        // Format 9: load/store with 5-bit immediate offset (word or byte).
        if ((op & 0xE000) == 0x6000)
        {
            var rd = op & 7;
            var isByte = ((op >> 12) & 1) != 0;
            var size = isByte ? 1 : 4;
            var addr = unchecked(_r[(op >> 3) & 7] + (uint)(((op >> 6) & 0x1F) * (isByte ? 1 : 4)));
            if (((op >> 11) & 1) != 0) _r[rd] = Load(rom, addr, size, false);
            else Store(addr, size, _r[rd]);
            return next;
        }

        // Format 10: load/store halfword with immediate offset.
        if ((op & 0xF000) == 0x8000)
        {
            var rd = op & 7;
            var addr = unchecked(_r[(op >> 3) & 7] + (uint)(((op >> 6) & 0x1F) * 2));
            if (((op >> 11) & 1) != 0) _r[rd] = Load(rom, addr, 2, false);
            else Store(addr, 2, _r[rd]);
            return next;
        }

        // Format 11: SP-relative load/store.
        if ((op & 0xF000) == 0x9000)
        {
            var rd = (op >> 8) & 7;
            var addr = unchecked(_r[13] + (uint)((byte)op * 4));
            if (((op >> 11) & 1) != 0) _r[rd] = Load(rom, addr, 4, false);
            else Store(addr, 4, _r[rd]);
            return next;
        }

        // Format 12: load address (PC- or SP-relative).
        if ((op & 0xF000) == 0xA000)
        {
            var rd = (op >> 8) & 7;
            var imm = (uint)((byte)op * 4);
            _r[rd] = ((op >> 11) & 1) != 0 ? unchecked(_r[13] + imm) : ((pc + 4) & ~3u) + imm;
            return next;
        }

        // Format 13: add/subtract an offset to SP.
        if ((op & 0xFF00) == 0xB000)
        {
            var imm = (uint)((op & 0x7F) * 4);
            _r[13] = ((op >> 7) & 1) != 0 ? unchecked(_r[13] - imm) : unchecked(_r[13] + imm);
            return next;
        }

        // Format 14: push/pop.
        if ((op & 0xF600) == 0xB400)
            return PushPop(rom, op, next);

        // Format 16: conditional branch.
        if ((op & 0xF000) == 0xD000)
        {
            var cond = (op >> 8) & 0xF;
            if (cond == 0xF) // Format 17: software interrupt
            {
                switch ((byte)op)
                {
                    case 0x06: // BIOS Div
                        SignedDivide();
                        return next;
                    case 0x08: // BIOS Sqrt
                        _r[0] = IntegerSqrt(_r[0]);
                        return next;
                    default:
                        throw new InvalidDataException(
                            $"unsupported GBA BIOS SWI 0x{(byte)op:X2} at 0x{pc:X8}");
                }
            }
            if (cond >= 0xE)
                throw new InvalidDataException($"unsupported conditional branch 0x{op:X4} at 0x{pc:X8}");
            var target = unchecked(pc + 4 + (uint)((sbyte)(byte)op * 2));
            return Condition(cond) ? target : next;
        }

        // Format 18: unconditional branch.
        if ((op & 0xF800) == 0xE000)
        {
            var offset = (op & 0x7FF) << 1;
            if ((offset & 0x800) != 0)
                offset -= 0x1000;
            return unchecked(pc + 4 + (uint)offset);
        }

        // Format 19: long branch with link (two halfwords).
        if ((op & 0xF800) == 0xF000)
        {
            var low = ReadHalf(rom, pc + 2);
            if ((low & 0xF800) != 0xF800)
                throw new InvalidDataException($"malformed BL pair at 0x{pc:X8}");
            var offset = ((op & 0x7FF) << 12) | ((low & 0x7FF) << 1);
            if ((offset & 0x400000) != 0)
                offset -= 0x800000;
            var target = unchecked(pc + 4 + (uint)offset);
            return CallOrHook(rom, target, pc + 4);
        }

        throw new InvalidDataException($"unsupported THUMB opcode 0x{op:X4} at 0x{pc:X8}");
    }

    private uint AluOp(ushort op, uint next)
    {
        var rd = op & 7;
        var rs = (op >> 3) & 7;
        switch ((op >> 6) & 0xF)
        {
            case 0x0: _r[rd] = SetNz(_r[rd] & _r[rs]); break;                 // and
            case 0x1: _r[rd] = SetNz(_r[rd] ^ _r[rs]); break;                 // eor
            case 0x2: _r[rd] = SetNz(Shift(0, _r[rd], (int)(_r[rs] & 0xFF))); break; // lsl
            case 0x3: _r[rd] = SetNz(Shift(1, _r[rd], (int)(_r[rs] & 0xFF))); break; // lsr
            case 0x4: _r[rd] = SetNz(Shift(2, _r[rd], (int)(_r[rs] & 0xFF))); break; // asr
            case 0x5: _r[rd] = AddFlags(_r[rd], _r[rs], _c ? 1u : 0u); break; // adc
            case 0x6: _r[rd] = AddFlags(_r[rd], ~_r[rs], _c ? 1u : 0u); break; // sbc
            case 0x8: SetNz(_r[rd] & _r[rs]); break;                          // tst
            case 0x9: _r[rd] = SubFlags(0, _r[rs]); break;                    // neg
            case 0xA: SubFlags(_r[rd], _r[rs]); break;                        // cmp
            case 0xB: AddFlags(_r[rd], _r[rs]); break;                        // cmn
            case 0xC: _r[rd] = SetNz(_r[rd] | _r[rs]); break;                 // orr
            case 0xD: _r[rd] = SetNz(unchecked((uint)((int)_r[rd] * (int)_r[rs]))); break; // mul
            case 0xE: _r[rd] = SetNz(_r[rd] & ~_r[rs]); break;                // bic
            case 0xF: _r[rd] = SetNz(~_r[rs]); break;                         // mvn
            default: throw new InvalidDataException($"unsupported ALU op 0x{op:X4}");
        }

        return next;
    }

    private uint PushPop(ReadOnlySpan<byte> rom, ushort op, uint next)
    {
        var list = (byte)op;
        var extra = ((op >> 8) & 1) != 0;
        if (((op >> 11) & 1) == 0) // push
        {
            if (extra)
            {
                _r[13] -= 4;
                Store(_r[13], 4, _r[14]);
            }

            for (var i = 7; i >= 0; i--)
            {
                if ((list & (1 << i)) == 0)
                    continue;
                _r[13] -= 4;
                Store(_r[13], 4, _r[i]);
            }

            return next;
        }

        for (var i = 0; i < 8; i++)
        {
            if ((list & (1 << i)) == 0)
                continue;
            _r[i] = Load(rom, _r[13], 4, false);
            _r[13] += 4;
        }

        if (!extra)
            return next;
        var pcValue = Load(rom, _r[13], 4, false);
        _r[13] += 4;
        return pcValue;
    }

    // A BL either lands on one of the hooked ROM routines, or is a genuine call
    // (several accessors compose others).
    private uint CallOrHook(ReadOnlySpan<byte> rom, uint target, uint returnTo)
    {
        switch (target)
        {
            case HookDivideByCell:
            case HookLaterDivideByCell:
            case HookThps4DivideByCell:
            case HookThps3DivideByCell:
                _r[0] = unchecked((uint)((int)_r[0] / CellSpan));
                return returnTo;
            case HookSignedDivide:
            case HookLaterSignedDivide:
            case HookThps4SignedDivide:
                SignedDivide();
                return returnTo;
            case HookLaterRemainder:
            {
                var divisor = (int)_r[1];
                _r[0] = divisor == 0 ? 0u : unchecked((uint)((int)_r[0] % divisor));
                return returnTo;
            }
            case HookSqrt:
            case HookLaterSqrt:
            case HookThps4Sqrt:
                _r[0] = IntegerSqrt(_r[0]);
                return returnTo;
            case HookThps3DynamicHeightQuery:
            case HookThps3ObjectIdExists:
            case HookThps3ObjectByteExists:
                _r[0] = 0;
                return returnTo;
            default:
                if (target < RomBase || target >= RomBase + (uint)rom.Length)
                    throw new InvalidDataException($"unhandled BL to 0x{target:X8}");
                _r[14] = returnTo | 1;
                return target;
        }
    }

    private void SignedDivide()
    {
        var numerator = (int)_r[0];
        var denominator = (int)_r[1];
        if (denominator == 0)
        {
            _r[0] = _r[1] = _r[3] = 0;
            return;
        }

        var quotient = numerator / denominator;
        _r[0] = unchecked((uint)quotient);
        _r[1] = unchecked((uint)(numerator % denominator));
        _r[3] = unchecked((uint)Math.Abs((long)quotient));
    }

    private static uint IntegerSqrt(uint value)
    {
        // A double represents every 32-bit integer exactly. Correct a possible
        // boundary rounding anyway so this remains the BIOS integer operation.
        var root = (uint)Math.Sqrt(value);
        while ((ulong)(root + 1) * (root + 1) <= value)
            root++;
        while ((ulong)root * root > value)
            root--;
        return root;
    }

    private uint Shift(int kind, uint value, int amount)
    {
        switch (kind)
        {
            case 0: // LSL
                if (amount == 0)
                    return value;
                if (amount > 32)
                    return 0;
                _c = amount <= 32 && ((value >> (32 - amount)) & 1) != 0;
                return amount < 32 ? value << amount : 0u;
            case 1: // LSR
                if (amount == 0)
                    amount = 32;
                _c = amount <= 32 && ((value >> (amount - 1)) & 1) != 0;
                return amount < 32 ? value >> amount : 0u;
            default: // ASR
                if (amount is 0 or > 31)
                    amount = 31;
                _c = (((int)value >> (amount - 1)) & 1) != 0;
                return unchecked((uint)((int)value >> amount));
        }
    }

    private uint SetNz(uint value)
    {
        _n = (value >> 31) != 0;
        _z = value == 0;
        return value;
    }

    private uint AddFlags(uint a, uint b, uint carry = 0)
    {
        var wide = (ulong)a + b + carry;
        var result = (uint)wide;
        _c = wide > 0xFFFFFFFFUL;
        _v = ((a ^ result) & (b ^ result) & 0x80000000u) != 0;
        _n = (result >> 31) != 0;
        _z = result == 0;
        return result;
    }

    private uint SubFlags(uint a, uint b) => AddFlags(a, ~b, 1);

    private bool Condition(int cond) => cond switch
    {
        0x0 => _z,
        0x1 => !_z,
        0x2 => _c,
        0x3 => !_c,
        0x4 => _n,
        0x5 => !_n,
        0x6 => _v,
        0x7 => !_v,
        0x8 => _c && !_z,
        0x9 => !_c || _z,
        0xA => _n == _v,
        0xB => _n != _v,
        0xC => !_z && _n == _v,
        _ => _z || _n != _v
    };

    private uint Load(ReadOnlySpan<byte> rom, uint address, int size, bool signed)
    {
        var source = Resolve(rom, address, size, out var offset);
        uint value = 0;
        for (var i = 0; i < size; i++)
            value |= (uint)source[offset + i] << (8 * i);
        if (!signed)
            return value;
        var signBit = 1u << (8 * size - 1);
        return (value & signBit) != 0 ? value | ~(signBit * 2 - 1) : value;
    }

    private void Store(uint address, int size, uint value)
    {
        if (address < StackTop - StackBytes || address + (uint)size > StackTop)
            throw new InvalidDataException($"store outside the modelled stack at 0x{address:X8}");
        var offset = (int)(address - (StackTop - StackBytes));
        for (var i = 0; i < size; i++)
            _stack[offset + i] = (byte)(value >> (8 * i));
    }

    private ReadOnlySpan<byte> Resolve(ReadOnlySpan<byte> rom, uint address, int size, out int offset)
    {
        if (address >= RomBase && address + (uint)size <= RomBase + (uint)rom.Length)
        {
            offset = (int)(address - RomBase);
            return rom;
        }

        if (address >= RecordAddress && address + (uint)size <= RecordAddress + (uint)_record.Length)
        {
            offset = (int)(address - RecordAddress);
            return _record;
        }

        if (address >= StackTop - StackBytes && address + (uint)size <= StackTop)
        {
            offset = (int)(address - (StackTop - StackBytes));
            return _stack;
        }

        if (address == DivideFnPointerSlot && size == 4)
        {
            offset = 0;
            return _dividePointer;
        }

        // A caller may publish a tiny, explicit snapshot of runtime scalar
        // state needed by an otherwise pure height accessor. Resolve it before
        // the explicit object-bank mapping below so a title-specific global can
        // never be mistaken for that bank pointer.
        if (size == 4
            && _runtimeWords != null
            && _runtimeWords.TryGetValue(address, out var modelledWord))
        {
            for (var i = 0; i < _runtimeWordPointer.Length; i++)
                _runtimeWordPointer[i] = (byte)(modelledWord >> (8 * i));
            offset = 0;
            return _runtimeWordPointer;
        }

        // A very small set of byte-sized loader/gameplay state reads is supplied
        // explicitly by the caller. Unknown EWRAM must fail closed: treating any
        // byte read as zero can turn a live-state dependency into plausible but
        // fabricated static geometry.
        if (size == 1
            && _runtimeBytes != null
            && _runtimeBytes.TryGetValue(address, out var modelledByte))
        {
            _runtimeByte[0] = modelledByte;
            offset = 0;
            return _runtimeByte;
        }

        // Composite height objects read the copied height-object bank through one
        // exact loader-published global. Both the address and the ROM-equivalent
        // bank value are supplied by the format decoder; no other EWRAM word is
        // allowed to alias it.
        if (size == 4
            && _runtimeObjectBank != 0
            && _runtimeObjectBankAddress != 0
            && address == _runtimeObjectBankAddress)
        {
            offset = 0;
            return _runtimeObjectBankPointer;
        }

        throw new InvalidDataException($"load outside the modelled memory map at 0x{address:X8}");
    }

    // The accessors dereference this slot and branch through it; the divide hook
    // supplies the behaviour, so the value only has to be the hooked address.
    private static readonly byte[] _dividePointer = [0x51, 0x79, 0x03, 0x08];

    private static ushort ReadHalf(ReadOnlySpan<byte> rom, uint address)
    {
        if (address < RomBase || address + 2 > RomBase + (uint)rom.Length)
            throw new InvalidDataException($"instruction fetch outside ROM at 0x{address:X8}");
        var offset = (int)(address - RomBase);
        return (ushort)(rom[offset] | (rom[offset + 1] << 8));
    }
}

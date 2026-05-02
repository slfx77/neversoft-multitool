"""Walk VIF opcodes in THAW PS2 .skin.ps2 files to understand the rendering chain structure."""
import struct, sys, os

def vif_next(data, off, end):
    if off >= end or off + 4 > len(data): return end
    cmd = data[off + 3]
    if (cmd & 0x60) != 0x60:
        c = cmd & 0x7F
        if c in (0,1,2,3,4,5,6,7,0x10,0x11,0x13,0x14,0x15,0x17): return off + 4
        if c == 0x20: return off + 8
        if c in (0x30, 0x31): return off + 20
        if c == 0x4A: return off + (data[off+2]<<3) + 4
        if c in (0x50, 0x51): return off + (struct.unpack_from('<H', data, off)[0] << 4) + 4
        return end
    vn = (cmd >> 2) & 3; vl = cmd & 3; num = data[off + 2]
    if num == 0: num = 256
    return off + 4 + ((((32 >> vl) * (vn+1) * num + 31) >> 5) << 2)

def walk_file(path):
    with open(path, 'rb') as f:
        data = f.read()

    nObj, tm1, tm2, dSize = struct.unpack_from('<4I', data, 0)
    entry_end = 32 + nObj*8 + tm2*64
    print(f"File: {os.path.basename(path)}")
    print(f"Header: numObj={nObj}, tm1={tm1}, tm2={tm2}, dataSize={dSize}, fileSize={len(data)}")
    print(f"Entry table end: 0x{entry_end:X}")

    # Find first FLUSH (0x11) or FLUSHE (0x10) as VIF start
    vif_start = None
    for i in range(entry_end, min(entry_end + 0x4000, len(data)-3), 4):
        c = data[i+3] & 0x7F
        if c in (0x10, 0x11):
            vif_start = i
            break

    if vif_start is None:
        print("ERROR: No FLUSH found!")
        return

    print(f"VIF start (FLUSH): 0x{vif_start:X} (gap = {vif_start - entry_end} bytes)")

    # Read DMA tag header at vif_start - 8
    if vif_start >= 8:
        dma_raw = struct.unpack_from('<II', data, vif_start - 8)
        qwc = dma_raw[0] & 0xFFFF
        dma_id = (dma_raw[0] >> 28) & 0xF
        print(f"DMA tag at 0x{vif_start-8:X}: QWC={qwc}, ID={dma_id}, raw=0x{dma_raw[0]:08X} 0x{dma_raw[1]:08X}")

    # Walk VIF
    pos = vif_start
    end = len(data)
    count = 0
    in_interleaved = False
    total_verts = 0
    batch_count = 0
    mesh_count = 0
    flush_count = 0

    NAMES = {(0,0):'S_32',(1,0):'V2_32',(1,1):'V2_16',(2,0):'V3_32',(2,1):'V3_16',
             (2,2):'V3_8',(3,0):'V4_32',(3,1):'V4_16',(3,2):'V4_8'}
    CMD_NAMES = {0:'NOP',1:'STCYCL',2:'OFFSET',3:'BASE',4:'ITOP',5:'STMOD',6:'MSKPATH3',
                 7:'MARK',0x10:'FLUSHE',0x11:'FLUSH',0x13:'FLUSHA',0x14:'MSCAL',0x15:'MSCALF',
                 0x17:'MSCNT',0x20:'STMASK',0x30:'STROW',0x31:'STCOL',0x4A:'MPG',
                 0x50:'DIRECT',0x51:'DIRECTHL'}

    while pos < end and pos + 4 <= len(data) and count < 2000:
        cmd = data[pos+3]; c = cmd & 0x7F

        if (cmd & 0x60) == 0x60:
            vn = (cmd >> 2) & 3; vl = cmd & 3; num = data[pos+2]
            actual = 256 if num == 0 else num
            n = NAMES.get((vn,vl), f'UNK_vn{vn}_vl{vl}')
            extra = ''
            if in_interleaved and num > 1:
                if vn==2 and vl==1: extra=' <-- POS'; total_verts += actual; batch_count += 1
                elif vn==2 and vl==2: extra=' <-- NRM'
                elif vn==3 and vl==1: extra=' <-- UV+ADC'
            print(f'  0x{pos:04X}: UNPACK {n} num={actual}{extra}')
        elif c == 0x01:
            cl, wl = data[pos], data[pos+1]
            print(f'  0x{pos:04X}: STCYCL CL={cl} WL={wl}')
            if cl == 3 and wl == 1: in_interleaved = True
            elif cl == 1 and wl == 1: in_interleaved = False
        elif c == 0x17:
            print(f'  0x{pos:04X}: MSCNT')
        elif c == 0x00:
            pass  # NOP, skip
        elif c == 0x11:
            flush_count += 1
            if flush_count > 1:
                mesh_count += 1
                # Check for DMA tag 8 bytes before this FLUSH
                if pos >= 8:
                    dma = struct.unpack_from('<II', data, pos - 8)
                    q = dma[0] & 0xFFFF
                    print(f'  --- MESH BOUNDARY (DMA QWC={q}) ---')
            print(f'  0x{pos:04X}: FLUSH')
        elif c in (0x50, 0x51):
            qw = struct.unpack_from('<H', data, pos)[0]
            print(f'  0x{pos:04X}: {CMD_NAMES[c]} {qw}QW')
        elif c in CMD_NAMES:
            print(f'  0x{pos:04X}: {CMD_NAMES[c]}')
        else:
            print(f'  0x{pos:04X}: UNKNOWN 0x{c:02X} (raw=0x{struct.unpack_from("<I",data,pos)[0]:08X})')
            # Try to skip 8 bytes (DMA tag) and continue
            skip_pos = pos + 8
            if skip_pos + 4 <= len(data):
                sc = data[skip_pos + 3] & 0x7F
                if sc in (0x10, 0x11):
                    print(f'  [Skipping 8-byte DMA tag, next FLUSH at 0x{skip_pos:X}]')
                    pos = skip_pos
                    continue
            break

        nxt = vif_next(data, pos, end)
        if nxt <= pos: break
        pos = nxt; count += 1

    print(f'\nTotal opcodes: {count}, ended at 0x{pos:X}')
    print(f'Position batches: {batch_count}, Total vertices: {total_verts}')
    print(f'FLUSH count: {flush_count} (mesh boundaries: {mesh_count})')
    print(f'Expected triangles (approx): {total_verts - 2*batch_count}')

if __name__ == '__main__':
    sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'utilities'))
    from sample_paths import find as _find_sample

    BUILD = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)"
    backpack = _find_sample(BUILD, "acc_backpack01.skin.ps2")
    hawk = _find_sample(BUILD, "skater_hawk.skin.ps2")
    if backpack is None or hawk is None:
        sys.exit("Sample build not populated; run tools/SampleGenerator")

    walk_file(str(backpack))
    print("\n" + "="*80 + "\n")
    walk_file(str(hawk))

# Credit to JayRedFox: https://github.com/JayFoxRox/thps2-tools/blob/master/common.py

import struct
import os


def read_string(f, n=0):
    if n == 0:
        s = b""
        while True:
            c = f.read(1)
            if c == b"\0":
                break
            s += c
    else:
        s = f.read(n)
        s = s.partition(b"\0")[0]
    try:
        s = s.decode("ascii")
    except:
        s = s.decode("latin-1")  # FIXME: This is a poor fallback
    return s


def read8(f):
    return struct.unpack("<B", f.read(1))[0]


def read16(f):
    return struct.unpack("<H", f.read(2))[0]


def read16s(f):
    return struct.unpack("<h", f.read(2))[0]


def read32(f):
    return struct.unpack("<I", f.read(4))[0]


def read32s(f):
    return struct.unpack("<i", f.read(4))[0]


def read_float(f):
    return struct.unpack("<f", f.read(4))[0]


def read_struct(f, fmt):
    size = struct.calcsize(fmt)
    return struct.unpack(fmt, f.read(size))


def align(f, n):
    return f.read((n - (f.tell() % n)) % n)


def is_repeating(p, v):
    for x in list(p):
        if x != v:
            return False
    return True


def code_string(s):
    s = s.encode("unicode_escape")
    s = s.decode("utf-8")
    return '"%s"' % s.replace('"', '\\"')


def code_float(f):
    # FIXME: Remove trailing zeroes
    return "%f" % f


def crc32(data, start=0xFFFFFFFF):
    result = start
    for byte in data:
        mask = result ^ byte
        for _ in range(8):
            result = ((result << 1) | (result >> 31)) & 0xFFFFFFFF
            if mask & 1:
                result ^= 0xEDB88320
            mask >>= 1

    # FIXME: Make this work somehow?
    if False:
        import zlib

        ref = zlib.crc32(data, 0xFFFFFFFF)
        ref2 = ref ^ 0xFFFFFFFF
        print("%08X == %08X (needs to be %08X)" % (ref, ref2, result))

    return result


class _FileWriter:
    def __init__(self):
        self._contents = []

    def _write(self, data):
        self._contents += [data]

    def Save(self, path):
        with open(path, "wb") as fo:
            fo.write("".join(self._contents).encode("utf-8"))


class WavefrontMtl(_FileWriter):
    def __init__(self):
        _FileWriter.__init__(self)

    def NewMaterial(self, name):
        # FIXME: How to handle spaces?
        self._write("newmtl %s\n" % name)

    def _map(self, target, name, scale=None):
        line = "map_%s" % target
        if scale != None:
            line += " -s %f %f %f" % scale
        # FIXME: Bugs in blender prevent use of quotation marks? Debug..
        #       For now, replace spaces by underscore
        line += " %s\n" % name.replace(" ", "_")
        self._write(line)

    def IlluminationMode(self, mode):
        self._write("illum %d\n" % mode)

    def DiffuseMap(self, name, scale=None):
        self._map("Kd", name, scale)

    def DissolveMap(self, name, scale=None):
        self._map("d", name, scale)


class WavefrontObj(_FileWriter):
    def __init__(self):
        _FileWriter.__init__(self)
        self._vertex_count = 0
        self._normal_count = 0
        self._texture_coordinate_count = 0

    def Object(self, name):
        self._write("o %s\n" % name)

    def MaterialLibrary(self, name):
        self._write("mtllib %s\n" % name)

    def UseMaterial(self, name):
        self._write("usemtl %s\n" % name)

    def Comment(self, comment):
        # FIXME: Split by line and ensure "# " prefix
        self._write("# %s\n" % comment)

    def Vertex(self, x, y, z):
        self._write("v %f %f %f\n" % (x, y, z))
        self._vertex_count += 1
        return self._vertex_count

    def TextureCoordinate(self, u, v):
        self._write("vt %f %f\n" % (u, v))
        self._texture_coordinate_count += 1
        return self._texture_coordinate_count

    def Normal(self, x, y, z):
        self._write("vn %f %f %f\n" % (x, y, z))
        self._normal_count += 1
        return self._normal_count

    def Face(self, vertex_indices, texture_coordinate_indices, normal_indices):
        assert texture_coordinate_indices == None or len(texture_coordinate_indices) == len(vertex_indices)
        assert normal_indices == None or len(normal_indices) == len(vertex_indices)

        line = "f"
        for i, vertex_index in enumerate(vertex_indices):

            line += " %d" % vertex_index

            # Helper to keep a clean file
            def index(line, indices, skipped=0):
                if indices != None:
                    line += "/" * skipped + "/%d" % indices[i]
                    return line, 0
                return line, skipped + 1

            # Write additional information
            line, skipped = index(line, texture_coordinate_indices)
            line, skipped = index(line, normal_indices, skipped)

        line += "\n"

        self._write(line)


def get_file_extension(file_path):
    return file_path.split(".")[-1].upper()


def get_filename_without_extension(file_path):
    return os.path.splitext(os.path.basename(file_path))[0]

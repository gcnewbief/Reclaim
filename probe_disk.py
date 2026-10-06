"""Read-only probe of a physical drive - never writes. Usage: py probe_disk.py [n]

First ~1GB of this drive was wiped in a test. NTFS $MFT lives ~3GB in, so
this scans a dense window around there for FILE records and tries to parse
a few filenames to prove the approach.
"""
import ctypes
import struct
import sys

GENERIC_READ = 0x80000000
FILE_SHARE_RW = 3
OPEN_EXISTING = 3
INVALID = ctypes.c_void_p(-1).value

n = sys.argv[1] if len(sys.argv) > 1 else "1"
path = rf"\\.\PhysicalDrive{n}"
k = ctypes.windll.kernel32
k.CreateFileW.restype = ctypes.c_void_p
k.DeviceIoControl.restype = ctypes.c_bool
k.SetFilePointerEx.restype = ctypes.c_bool
k.ReadFile.restype = ctypes.c_bool
k.CloseHandle.restype = ctypes.c_bool
h = k.CreateFileW(path, GENERIC_READ, FILE_SHARE_RW, None, OPEN_EXISTING, 0, None)
if not h or h == INVALID:
    print(f"{path}: open failed err={k.GetLastError()} (5 = need admin)")
    sys.exit(1)

IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C
out = ctypes.create_string_buffer(16)
ret = ctypes.c_ulong(0)
size = 0
if k.DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, None, 0, out, 16, ctypes.byref(ret), None):
    size = struct.unpack("<q", out.raw[:8])[0]
    print(f"{path}: {size:,} bytes ({size/1e9:.1f} GB)")

def read_at(off, length):
    li = ctypes.c_longlong(off)
    if not k.SetFilePointerEx(h, li, None, 0):
        return None
    buf = ctypes.create_string_buffer(length)
    nr = ctypes.c_ulong(0)
    if not k.ReadFile(h, buf, length, ctypes.byref(nr), None) or nr.value == 0:
        return None
    return buf.raw[:nr.value]

def parse_filename(rec):
    """Minimal $FILE_NAME attr walk on a 1KB MFT record. Returns (name, flags, size)."""
    try:
        if rec[0:4] != b"FILE":
            return None
        fixu_off = struct.unpack("<H", rec[4:6])[0]
        fixu_cnt = struct.unpack("<H", rec[6:8])[0]
        # apply fixup: last 2 bytes of each 512B sector replaced by usa entries
        rec = bytearray(rec)
        for i in range(1, fixu_cnt):
            usa = struct.unpack("<H", rec[fixu_off + i*2: fixu_off + i*2 + 2])[0]
            rec[i*512 - 2] = usa & 0xFF
            rec[i*512 - 1] = usa >> 8
        flags = struct.unpack("<H", rec[22:24])[0]   # 0x01 in-use, 0x02 dir
        attr_off = struct.unpack("<H", rec[20:22])[0]
        pos = attr_off
        for _ in range(40):
            atype = struct.unpack("<I", rec[pos:pos+4])[0]
            if atype == 0xFFFFFFFF:
                break
            alen = struct.unpack("<I", rec[pos+4:pos+8])[0]
            if atype == 0x30:  # $FILE_NAME
                nonres = rec[pos+8]
                coff = struct.unpack("<H", rec[pos+20:pos+22])[0]
                c = rec[pos+coff:pos+alen]
                nlen = c[64]
                name = c[66:66+nlen*2].decode("utf-16-le", "replace")
                real = struct.unpack("<Q", c[48:56])[0]
                return name, flags, real, nonres
            pos += alen
    except Exception:
        return None
    return None

# dense scan 1GB..5GB for FILE records (MFT zone is ~3GB into a typical volume)
found_recs = 0
names = []
first_file_at = None
start = 1 * 1024**3
end = min(5 * 1024**3, size or 5 * 1024**3)
chunk = 8 * 1024 * 1024
for base in range(start, end, chunk):
    data = read_at(base, chunk)
    if data is None:
        continue
    idx = 0
    while True:
        idx = data.find(b"FILE", idx)
        if idx < 0:
            break
        # FILE records are sector aligned; record itself can be FILE*/FILE0/FILE
        if idx % 512 == 0 and data[idx+4:idx+8] in (b"\x30\x00\x2a\x00",) or idx % 512 == 0:
            rec = data[idx:idx+1024]
            r = parse_filename(rec)
            if r:
                found_recs += 1
                if first_file_at is None:
                    first_file_at = base + idx
                if len(names) < 25:
                    names.append((base + idx, r))
        idx += 4

print(f"\nFILE records with parseable names in {start>>30}-{end>>30}GB: {found_recs}")
print(f"first at offset {first_file_at:,} ({first_file_at/1024**3:.2f} GB)" if first_file_at else "none found")
for off, (name, flags, real, nonres) in names:
    state = "LIVE" if flags & 1 else "DEL "
    kind = "dir " if flags & 2 else "file"
    res = "nonresident" if nonres else "resident"
    print(f"  {state} {kind} {res:<11} {real:>12,} B  {name}")

k.CloseHandle(h)

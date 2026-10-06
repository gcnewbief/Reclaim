"""Check absolute-offset alignment of FILE records in 1.4-2.0GB.
Buckets hits by offset % 2048 and validates fixup so we know which
alignment actually carries real records.
"""
import ctypes
import struct
import sys

GENERIC_READ = 0x80000000
path = r"\\.\PhysicalDrive1"
k = ctypes.windll.kernel32
k.CreateFileW.restype = ctypes.c_void_p
k.SetFilePointerEx.restype = ctypes.c_bool
k.ReadFile.restype = ctypes.c_bool
h = k.CreateFileW(path, GENERIC_READ, 3, None, 3, 0, None)
if not h or h == ctypes.c_void_p(-1).value:
    print(f"open failed err={k.GetLastError()}")
    sys.exit(1)

def read_at(off, length):
    li = ctypes.c_longlong(off)
    k.SetFilePointerEx(h, li, None, 0)
    buf = ctypes.create_string_buffer(length)
    nr = ctypes.c_ulong(0)
    k.ReadFile(h, buf, length, ctypes.byref(nr), None)
    return buf.raw[:nr.value]

def fixup_ok(rec):
    usa_off, usa_cnt = struct.unpack("<HH", rec[4:8])
    if usa_off < 0x28 or usa_cnt < 2 or usa_cnt > 10:
        return False
    usn = struct.unpack("<H", rec[usa_off:usa_off+2])[0]
    for i in range(1, usa_cnt):
        tail = i * 512 - 2
        if tail + 2 > len(rec):
            return False
        if struct.unpack("<H", rec[tail:tail+2])[0] != usn:
            return False
    return True

buckets = {}
fix_ok = 0
first = None
start, end = 1400 * 1024**2, 2000 * 1024**2
chunk = 8 * 1024**2
for base in range(start, end, chunk):
    data = read_at(base, chunk)
    idx = 0
    while True:
        idx = data.find(b"FILE", idx)
        if idx < 0:
            break
        abs_off = base + idx
        if abs_off % 512 == 0:
            b = abs_off % 2048
            buckets[b] = buckets.get(b, 0) + 1
            if fixup_ok(data[idx:idx+1024]):
                fix_ok += 1
                if first is None:
                    first = abs_off
        idx += 4

print(f"512-aligned FILE hits by offset%2048: {dict(sorted(buckets.items()))}")
print(f"of which pass fixup validation: {fix_ok}")
print(f"first valid at {first:,} = {first/1024**3:.3f} GB, %1024={first%1024}, %2048={first%2048}" if first else "none")

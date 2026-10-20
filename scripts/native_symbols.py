"""Check that a Windows native PDB belongs to the accompanying PE image."""
import struct
from pathlib import Path


def verify_pdb(dll, pdb):
    image = Path(dll).read_bytes()
    offset = image.index(b'RSDS')
    image_id = image[offset + 4:offset + 20]
    image_age = struct.unpack_from('<I', image, offset + 20)[0]
    data = Path(pdb).read_bytes()
    if not data.startswith(b'Microsoft C/C++ MSF 7.00\r\n\x1aDS\0\0\0'):
        raise ValueError('Expected a native MSF 7 PDB')
    block_size = struct.unpack_from('<I', data, 32)[0]
    directory_size = struct.unpack_from('<I', data, 44)[0]
    block_map = struct.unpack_from('<I', data, 52)[0]
    count = (directory_size + block_size - 1) // block_size
    blocks = struct.unpack_from(f'<{count}I', data, block_map * block_size)
    directory = b''.join(data[b * block_size:(b + 1) * block_size] for b in blocks)[:directory_size]
    streams = struct.unpack_from('<I', directory)[0]
    sizes = struct.unpack_from(f'<{streams}I', directory, 4)
    cursor = 4 + 4 * streams
    for index, size in enumerate(sizes):
        n = 0 if size == 0xffffffff else (size + block_size - 1) // block_size
        stream_blocks = struct.unpack_from(f'<{n}I', directory, cursor)
        cursor += 4 * n
        if index == 1:
            info = b''.join(data[b * block_size:(b + 1) * block_size] for b in stream_blocks)[:size]
            age = struct.unpack_from('<I', info, 8)[0]
            if info[12:28] != image_id or age != image_age:
                raise ValueError('Native PDB GUID/age does not match DLL CodeView record')
            return {'guid_bytes': image_id.hex(), 'age': age}
    raise ValueError('PDB information stream missing')

# SPDX-License-Identifier: GPL-3.0-or-later
"""Minimal reader for the binary glTF files written by SMConverter (-gltf).

Blender's glTF importer only creates images that a standard material slot uses,
so SM specific textures (ASG) are read straight from the file here.
"""

import json
import struct

GLB_MAGIC = 0x46546C67
CHUNK_JSON = 0x4E4F534A
CHUNK_BIN = 0x004E4942


class GlbFile:
    def __init__(self, path):
        with open(path, "rb") as f:
            data = f.read()

        magic, _version, length = struct.unpack_from("<III", data, 0)
        if magic != GLB_MAGIC:
            raise ValueError("Not a binary glTF file: " + path)

        self.json = {}
        self.bin = b""
        offset = 12
        while offset + 8 <= min(length, len(data)):
            chunk_len, chunk_type = struct.unpack_from("<II", data, offset)
            offset += 8
            chunk = data[offset:offset + chunk_len]
            offset += chunk_len
            if chunk_type == CHUNK_JSON:
                self.json = json.loads(chunk.decode("utf-8"))
            elif chunk_type == CHUNK_BIN:
                self.bin = bytes(chunk)

    @property
    def is_smconverter(self):
        gen = self.json.get("asset", {}).get("generator", "")
        return "SMConverter" in gen

    def surface_presets(self):
        """sm_surface_presets of the root node (written by SMConverter), or None."""
        for node in self.json.get("nodes", []):
            presets = node.get("extras", {}).get("sm_surface_presets")
            if presets:
                return presets
        return None

    def texture_image(self, texture_index):
        """Returns (name, png_bytes) for a glTF texture index, or None."""
        textures = self.json.get("textures", [])
        if texture_index is None or texture_index < 0 or texture_index >= len(textures):
            return None

        image_index = textures[texture_index].get("source")
        images = self.json.get("images", [])
        if image_index is None or image_index >= len(images):
            return None

        image = images[image_index]
        view_index = image.get("bufferView")
        if view_index is None:
            return None

        view = self.json["bufferViews"][view_index]
        start = view.get("byteOffset", 0)
        data = self.bin[start:start + view["byteLength"]]
        return image.get("name", "texture_%d" % image_index), data

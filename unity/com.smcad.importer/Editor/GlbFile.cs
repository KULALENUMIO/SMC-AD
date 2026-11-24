// SPDX-License-Identifier: MIT
// Reader for the binary glTF written by SMConverter (-gltf).
// Supports what the converter emits: float VEC2/VEC3/VEC4/SCALAR accessors, uint32/uint16 indices,
// PNG images in buffer views, node TRS, extras.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SMCAD.Editor
{
    internal sealed class GlbFile
    {
        public Dictionary<string, object> Json;
        public byte[] Bin;
        public string Path;

        public static GlbFile Load(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 20 || BitConverter.ToUInt32(data, 0) != 0x46546C67)
                throw new InvalidDataException("バイナリの glTF（.glb）ではありません: " + path);

            var glb = new GlbFile { Path = path };
            int length = (int)Math.Min(BitConverter.ToUInt32(data, 8), (uint)data.Length);
            int offset = 12;
            while (offset + 8 <= length)
            {
                int chunkLen = (int)BitConverter.ToUInt32(data, offset);
                uint chunkType = BitConverter.ToUInt32(data, offset + 4);
                offset += 8;
                if (chunkType == 0x4E4F534A)
                    glb.Json = (Dictionary<string, object>)MiniJson.Parse(Encoding.UTF8.GetString(data, offset, chunkLen));
                else if (chunkType == 0x004E4942)
                {
                    glb.Bin = new byte[chunkLen];
                    Buffer.BlockCopy(data, offset, glb.Bin, 0, chunkLen);
                }
                offset += chunkLen;
            }

            if (glb.Json == null) throw new InvalidDataException("glb has no JSON chunk");
            return glb;
        }

        public bool IsSMConverter
            => (MiniJson.Str(MiniJson.Obj(Json, "asset"), "generator") ?? "").Contains("SMConverter");

        public List<object> List(string key) => MiniJson.Arr(Json, key) ?? new List<object>();

        // Raw bytes of a buffer view
        public ArraySegment<byte> View(int viewIndex)
        {
            var view = List("bufferViews")[viewIndex];
            int start = MiniJson.Int(view, "byteOffset", 0);
            int len = MiniJson.Int(view, "byteLength", 0);
            return new ArraySegment<byte>(Bin, start, len);
        }

        static int ComponentCount(string type)
        {
            switch (type)
            {
                case "SCALAR": return 1;
                case "VEC2": return 2;
                case "VEC3": return 3;
                case "VEC4": return 4;
                default: throw new NotSupportedException("Accessor type " + type);
            }
        }

        // Reads a float accessor into a flat array
        public float[] ReadFloats(int accessorIndex, out int components)
        {
            var acc = List("accessors")[accessorIndex];
            components = ComponentCount(MiniJson.Str(acc, "type"));
            int count = MiniJson.Int(acc, "count", 0);
            if (MiniJson.Int(acc, "componentType") != 5126)
                throw new NotSupportedException("頂点データは float のアクセサーにしか対応していません");

            var view = List("bufferViews")[MiniJson.Int(acc, "bufferView")];
            int start = MiniJson.Int(view, "byteOffset", 0) + MiniJson.Int(acc, "byteOffset", 0);
            int stride = MiniJson.Int(view, "byteStride", 0);
            if (stride <= 0) stride = components * 4;

            var result = new float[count * components];
            for (int i = 0; i < count; i++)
                Buffer.BlockCopy(Bin, start + i * stride, result, i * components * 4, components * 4);
            return result;
        }

        public int[] ReadIndices(int accessorIndex)
        {
            var acc = List("accessors")[accessorIndex];
            int count = MiniJson.Int(acc, "count", 0);
            int type = MiniJson.Int(acc, "componentType");
            var view = List("bufferViews")[MiniJson.Int(acc, "bufferView")];
            int start = MiniJson.Int(view, "byteOffset", 0) + MiniJson.Int(acc, "byteOffset", 0);

            var result = new int[count];
            switch (type)
            {
                case 5125: for (int i = 0; i < count; i++) result[i] = (int)BitConverter.ToUInt32(Bin, start + i * 4); break;
                case 5123: for (int i = 0; i < count; i++) result[i] = BitConverter.ToUInt16(Bin, start + i * 2); break;
                case 5121: for (int i = 0; i < count; i++) result[i] = Bin[start + i]; break;
                default: throw new NotSupportedException("Index component type " + type);
            }
            return result;
        }

        // PNG bytes + name of a glTF texture
        public bool TryGetTexture(int textureIndex, out string name, out byte[] png)
        {
            name = null; png = null;
            var textures = List("textures");
            if (textureIndex < 0 || textureIndex >= textures.Count) return false;
            int source = MiniJson.Int(textures[textureIndex], "source");
            var images = List("images");
            if (source < 0 || source >= images.Count) return false;
            var image = images[source];
            int viewIndex = MiniJson.Int(image, "bufferView");
            if (viewIndex < 0) return false;
            name = MiniJson.Str(image, "name", "texture_" + source);
            var seg = View(viewIndex);
            png = new byte[seg.Count];
            Buffer.BlockCopy(seg.Array, seg.Offset, png, 0, seg.Count);
            return true;
        }
    }
}

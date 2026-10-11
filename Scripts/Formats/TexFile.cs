using System;
using System.IO;

namespace PsdlViewer.Formats {
    /// <summary>Angel Studios texture (.tex). Only the first mip level is decoded.</summary>
    public class TexFile {
        public const int P8 = 1, PA8 = 14, P4_MC = 15, PA4_MC = 16, RGB888 = 17, RGBA8888 = 18;

        public int Width;
        public int Height;
        public int Type;
        public int Mips;
        /// <summary>RGBA8 pixels in file row order (the picture is stored bottom row first).</summary>
        public byte[] Rgba;
        public bool HasAlpha;

        /// <summary>Returns null when the texture type isn't supported.</summary>
        public static TexFile Load(string path) {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);
            var tex = new TexFile {
                Width = r.ReadUInt16(),
                Height = r.ReadUInt16(),
                Type = r.ReadUInt16(),
                Mips = r.ReadUInt16()
            };
            r.ReadUInt16();
            r.ReadUInt32();
            int w = tex.Width, h = tex.Height;
            if (w <= 0 || h <= 0) return null;
            var rgba = new byte[w * h * 4];
            switch (tex.Type) {
                case P8:
                case PA8:
                case P4_MC:
                case PA4_MC: {
                    int bits = tex.Type == P8 || tex.Type == PA8 ? 8 : 4;
                    int colors = 1 << bits;
                    byte[] palette = r.ReadBytes(colors * 4); // BGRA
                    bool alpha = tex.Type == PA8 || tex.Type == PA4_MC;
                    int pixelBytes = bits == 8 ? w * h : (w * h + 1) / 2;
                    byte[] data = r.ReadBytes(pixelBytes);
                    for (int i = 0; i < w * h; i++) {
                        int index;
                        if (bits == 8) index = i < data.Length ? data[i] : 0;
                        else {
                            int b = i / 2 < data.Length ? data[i / 2] : 0;
                            index = (i & 1) == 0 ? b & 0xf : b >> 4;
                        }
                        rgba[i * 4] = palette[index * 4 + 2];
                        rgba[i * 4 + 1] = palette[index * 4 + 1];
                        rgba[i * 4 + 2] = palette[index * 4];
                        rgba[i * 4 + 3] = alpha ? palette[index * 4 + 3] : (byte)255;
                    }
                    tex.HasAlpha = alpha;
                    break;
                }
                case RGB888:
                case RGBA8888: {
                    bool alpha = tex.Type == RGBA8888;
                    int bpp = alpha ? 4 : 3;
                    byte[] data = r.ReadBytes(w * h * bpp);
                    if (data.Length < w * h * bpp) Array.Resize(ref data, w * h * bpp);
                    // unlike the BGRA palettes, true color pixels are stored as RGB(A)
                    for (int i = 0; i < w * h; i++) {
                        rgba[i * 4] = data[i * bpp];
                        rgba[i * 4 + 1] = data[i * bpp + 1];
                        rgba[i * 4 + 2] = data[i * bpp + 2];
                        rgba[i * 4 + 3] = alpha ? data[i * bpp + 3] : (byte)255;
                    }
                    tex.HasAlpha = alpha;
                    break;
                }
                default:
                    return null;
            }
            tex.Rgba = rgba;
            return tex;
        }
    }
}

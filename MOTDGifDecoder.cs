using System;
using System.Collections.Generic;
using UnityEngine;

namespace WebsiteMOTD
{
    public struct GifFrame
    {
        public Texture2D Texture;
        public float Delay; // seconds
    }

    /// <summary>
    /// Pure-C# GIF89a decoder. Handles animated GIFs with LZW decompression,
    /// local/global color tables, transparency, and basic disposal methods.
    /// </summary>
    public static class GifDecoder
    {
        public static GifFrame[] Decode(byte[] data)
        {
            if (data == null || data.Length < 13) return null;

            string sig = System.Text.Encoding.ASCII.GetString(data, 0, 6);
            if (sig != "GIF87a" && sig != "GIF89a") return null;

            int pos = 6;

            // Logical Screen Descriptor
            int screenW = ReadU16(data, pos); pos += 2;
            int screenH = ReadU16(data, pos); pos += 2;
            byte lsd    = data[pos++];
            int bgIdx   = data[pos++];
            pos++; // pixel aspect ratio

            bool hasGct  = (lsd & 0x80) != 0;
            int  gctSize = (lsd & 0x07);

            Color32[] gct = null;
            if (hasGct)
                gct = ReadColorTable(data, ref pos, 2 << gctSize);

            var frames = new List<GifFrame>();

            // Per-frame graphics control state
            float delay        = 0.1f;
            bool  transparent  = false;
            int   transIdx     = 0;
            int   disposal     = 0;

            // Compositing canvas (RGBA)
            var canvas    = new Color32[screenW * screenH];
            var prevCanvas = new Color32[screenW * screenH];
            var bgColor   = (gct != null && bgIdx < gct.Length) ? gct[bgIdx] : new Color32(0, 0, 0, 0);

            for (int i = 0; i < canvas.Length; i++) canvas[i] = bgColor;

            while (pos < data.Length)
            {
                byte b = data[pos++];

                // ── Trailer ──
                if (b == 0x3B) break;

                // ── Extension ──
                if (b == 0x21)
                {
                    byte label = data[pos++];
                    if (label == 0xF9) // Graphic Control Extension
                    {
                        pos++; // block size (always 4)
                        byte gce = data[pos++];
                        delay      = Mathf.Max(ReadU16(data, pos) * 0.01f, 0.02f); pos += 2;
                        transIdx   = data[pos++];
                        pos++;   // block terminator

                        transparent = (gce & 0x01) != 0;
                        disposal    = (gce >> 3) & 0x07;
                    }
                    else
                    {
                        SkipSubBlocks(data, ref pos);
                    }
                    continue;
                }

                // ── Image Separator ──
                if (b == 0x2C)
                {
                    int imgLeft = ReadU16(data, pos); pos += 2;
                    int imgTop  = ReadU16(data, pos); pos += 2;
                    int imgW    = ReadU16(data, pos); pos += 2;
                    int imgH    = ReadU16(data, pos); pos += 2;
                    byte ipk    = data[pos++];

                    bool hasLct    = (ipk & 0x80) != 0;
                    bool interlace = (ipk & 0x40) != 0;
                    int  lctSize   = (ipk & 0x07);

                    Color32[] ct = gct;
                    if (hasLct)
                        ct = ReadColorTable(data, ref pos, 2 << lctSize);

                    byte lzwMin   = data[pos++];
                    byte[] imgData = ReadSubBlocks(data, ref pos);

                    int[] indices = LzwDecode(imgData, lzwMin, imgW * imgH);
                    if (indices == null || ct == null) continue;

                    if (interlace) indices = Deinterlace(indices, imgW, imgH);

                    // Apply disposal of previous frame
                    if (disposal == 2) // restore to background
                        for (int i = 0; i < canvas.Length; i++) canvas[i] = bgColor;
                    // disposal == 3 (restore to previous) is rare, skip for now

                    // Blit indices onto canvas
                    for (int row = 0; row < imgH; row++)
                    {
                        for (int col = 0; col < imgW; col++)
                        {
                            int si = row * imgW + col;
                            if (si >= indices.Length) continue;
                            int ci = indices[si];
                            if (transparent && ci == transIdx) continue;
                            int canvasIdx = (imgTop + row) * screenW + (imgLeft + col);
                            if (canvasIdx < 0 || canvasIdx >= canvas.Length) continue;
                            if (ci < 0 || ci >= ct.Length) continue;
                            canvas[canvasIdx] = ct[ci];
                        }
                    }

                    // Create texture (GIF origin is top-left; Unity is bottom-left → flip Y)
                    var tex = new Texture2D(screenW, screenH, TextureFormat.RGBA32, false);
                    var pixels = new Color32[canvas.Length];
                    for (int row = 0; row < screenH; row++)
                        Array.Copy(canvas, (screenH - 1 - row) * screenW, pixels, row * screenW, screenW);
                    tex.SetPixels32(pixels);
                    tex.Apply();

                    frames.Add(new GifFrame { Texture = tex, Delay = delay });

                    // Save canvas if next frame might want to restore to previous
                    Array.Copy(canvas, prevCanvas, canvas.Length);

                    // Reset per-frame state
                    delay       = 0.1f;
                    transparent = false;
                    disposal    = 0;
                    continue;
                }

                // Unknown block — try to skip
                if (pos < data.Length)
                    SkipSubBlocks(data, ref pos);
            }

            return frames.Count > 0 ? frames.ToArray() : null;
        }

        // ─── LZW Decompression ────────────────────────────────────────

        private static int[] LzwDecode(byte[] data, int minCodeSize, int pixelCount)
        {
            int clearCode  = 1 << minCodeSize;
            int eoiCode    = clearCode + 1;
            int codeSize   = minCodeSize + 1;
            int nextCode   = eoiCode + 1;
            int codeMask   = (1 << codeSize) - 1;

            // Dictionary: each entry is a sequence of color indices
            var dict = new List<int[]>(4096);
            ResetDict(dict, clearCode);

            var result = new List<int>(pixelCount + 64);
            int bitBuf = 0, bitsLeft = 0, dataPos = 0;
            int[] prev = null;

            while (result.Count < pixelCount)
            {
                // Fill bit buffer
                while (bitsLeft < codeSize)
                {
                    if (dataPos >= data.Length) goto done;
                    bitBuf |= data[dataPos++] << bitsLeft;
                    bitsLeft += 8;
                }
                int code = bitBuf & codeMask;
                bitBuf   >>= codeSize;
                bitsLeft  -= codeSize;

                if (code == clearCode)
                {
                    codeSize = minCodeSize + 1;
                    codeMask = (1 << codeSize) - 1;
                    nextCode = eoiCode + 1;
                    ResetDict(dict, clearCode);
                    prev = null;
                    continue;
                }

                if (code == eoiCode) break;

                int[] entry;
                if (code < dict.Count && dict[code] != null)
                {
                    entry = dict[code];
                }
                else if (code == nextCode && prev != null)
                {
                    // KwKw special case
                    entry = new int[prev.Length + 1];
                    Array.Copy(prev, entry, prev.Length);
                    entry[prev.Length] = prev[0];
                }
                else break; // malformed

                result.AddRange(entry);

                if (prev != null && nextCode < 4096)
                {
                    var ne = new int[prev.Length + 1];
                    Array.Copy(prev, ne, prev.Length);
                    ne[prev.Length] = entry[0];

                    if (nextCode < dict.Count) dict[nextCode] = ne;
                    else dict.Add(ne);
                    nextCode++;

                    if (nextCode > codeMask && codeSize < 12)
                    {
                        codeSize++;
                        codeMask = (1 << codeSize) - 1;
                    }
                }

                prev = entry;
            }

            done:
            return result.ToArray();
        }

        private static void ResetDict(List<int[]> dict, int clearCode)
        {
            dict.Clear();
            for (int i = 0; i < clearCode; i++) dict.Add(new[] { i });
            dict.Add(null); // clearCode placeholder
            dict.Add(null); // eoiCode placeholder
        }

        // ─── Helpers ─────────────────────────────────────────────────

        private static Color32[] ReadColorTable(byte[] data, ref int pos, int count)
        {
            var t = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                t[i] = new Color32(data[pos], data[pos + 1], data[pos + 2], 255);
                pos += 3;
            }
            return t;
        }

        private static byte[] ReadSubBlocks(byte[] data, ref int pos)
        {
            var buf = new List<byte>(256);
            while (pos < data.Length)
            {
                int sz = data[pos++];
                if (sz == 0) break;
                for (int i = 0; i < sz && pos < data.Length; i++)
                    buf.Add(data[pos++]);
            }
            return buf.ToArray();
        }

        private static void SkipSubBlocks(byte[] data, ref int pos)
        {
            while (pos < data.Length)
            {
                int sz = data[pos++];
                if (sz == 0) break;
                pos += sz;
            }
        }

        private static int ReadU16(byte[] data, int pos)
            => data[pos] | (data[pos + 1] << 8);

        private static int[] Deinterlace(int[] src, int w, int h)
        {
            var dst = new int[src.Length];
            int s = 0;
            foreach (int start in new[] { 0, 4, 2, 1 })
                foreach (int step in new[] { 8, 8, 4, 2 })
                    for (int y = start; y < h; y += step)
                        for (int x = 0; x < w; x++)
                            dst[y * w + x] = src[s++];
            return dst;
        }
    }
}

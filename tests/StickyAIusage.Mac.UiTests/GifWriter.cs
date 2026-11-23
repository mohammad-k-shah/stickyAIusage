namespace StickyAIusage.Mac.UiTests;

/// <summary>
/// Writes an animated GIF (GIF89a, looping) from frames of opaque BGRA pixels: each frame with a
/// palette of its own 256 most used colors (after dropping each channel to 5 bits; every pixel takes
/// its nearest palette color), so a light screen or a share card's gradient keeps its colors beside
/// the dark ones; LZW-coded image data, a delay per frame. Small and dependency-free: it only has
/// to encode the README's demo, whose frames are flat app colors and text.
/// </summary>
public static class GifWriter
{
    public sealed record Frame(byte[] Bgra, int DelayCentiseconds);

    public static byte[] Encode(int width, int height, IReadOnlyList<Frame> frames)
    {
        if (frames.Count == 0)
            throw new ArgumentException("No frames.", nameof(frames));
        foreach (var frame in frames)
            if (frame.Bgra.Length != width * height * 4)
                throw new ArgumentException("A frame's size differs from the picture's.", nameof(frames));

        var palettes = frames.Select(frame => Palette([frame])).ToList();
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write("GIF89a"u8.ToArray());
        writer.Write((ushort)width);
        writer.Write((ushort)height);
        // A global table too (the first frame's), for readers that look for one; each frame brings its own.
        writer.Write((byte)0xF7); // global table, 8 bits of color resolution, 256 entries
        writer.Write((byte)0);
        writer.Write((byte)0);
        WriteTable(writer, palettes[0]);
        // Loop forever (the NETSCAPE2.0 extension).
        writer.Write(new byte[] { 0x21, 0xFF, 0x0B });
        writer.Write("NETSCAPE2.0"u8.ToArray());
        writer.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });

        for (var f = 0; f < frames.Count; f++)
        {
            var frame = frames[f];
            var palette = palettes[f];
            writer.Write(new byte[] { 0x21, 0xF9, 0x04, 0x00 });
            writer.Write((ushort)frame.DelayCentiseconds);
            writer.Write(new byte[] { 0x00, 0x00 });
            writer.Write((byte)0x2C);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)width);
            writer.Write((ushort)height);
            writer.Write((byte)0x87); // a local table of 256 entries, not interlaced
            WriteTable(writer, palette);
            var lookup = new Dictionary<int, byte>();
            var indices = new byte[width * height];
            for (var p = 0; p < indices.Length; p++)
            {
                var b = frame.Bgra[p * 4];
                var g = frame.Bgra[p * 4 + 1];
                var r = frame.Bgra[p * 4 + 2];
                var rgb = (r << 16) | (g << 8) | b;
                if (!lookup.TryGetValue(rgb, out var index))
                    lookup[rgb] = index = Nearest(palette, r, g, b);
                indices[p] = index;
            }
            WriteImageData(writer, indices);
        }
        writer.Write((byte)0x3B);
        writer.Flush();
        return output.ToArray();
    }

    private static void WriteTable(BinaryWriter writer, List<int> palette)
    {
        for (var i = 0; i < 256; i++)
        {
            var color = i < palette.Count ? palette[i] : 0;
            writer.Write((byte)(color >> 16));
            writer.Write((byte)(color >> 8));
            writer.Write((byte)color);
        }
    }

    /// <summary>The most used colors of the frames, each the average of the pixels in its 5-bit box.</summary>
    private static List<int> Palette(IReadOnlyList<Frame> frames)
    {
        var counts = new Dictionary<int, (long Count, long R, long G, long B)>();
        foreach (var frame in frames)
            for (var p = 0; p < frame.Bgra.Length; p += 4)
            {
                int b = frame.Bgra[p], g = frame.Bgra[p + 1], r = frame.Bgra[p + 2];
                var key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                counts.TryGetValue(key, out var c);
                counts[key] = (c.Count + 1, c.R + r, c.G + g, c.B + b);
            }
        return counts.Values.OrderByDescending(c => c.Count).Take(256)
            .Select(c => (int)(c.R / c.Count) << 16 | (int)(c.G / c.Count) << 8 | (int)(c.B / c.Count)).ToList();
    }

    private static byte Nearest(List<int> palette, int r, int g, int b)
    {
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < palette.Count; i++)
        {
            int dr = (palette[i] >> 16 & 0xFF) - r, dg = (palette[i] >> 8 & 0xFF) - g, db = (palette[i] & 0xFF) - b;
            var distance = 3 * dr * dr + 4 * dg * dg + 2 * db * db;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return (byte)best;
    }

    /// <summary>Variable-width LZW of 8-bit indices, in sub-blocks of at most 255 bytes.</summary>
    private static void WriteImageData(BinaryWriter writer, byte[] indices)
    {
        const int minCodeSize = 8;
        const int clear = 1 << minCodeSize;
        const int end = clear + 1;
        writer.Write((byte)minCodeSize);

        var bytes = new List<byte>();
        int bitBuffer = 0, bitCount = 0;
        void Emit(int code, int size)
        {
            bitBuffer |= code << bitCount;
            bitCount += size;
            while (bitCount >= 8)
            {
                bytes.Add((byte)bitBuffer);
                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }

        var table = new Dictionary<int, int>();
        var codeSize = minCodeSize + 1;
        var next = end + 1;
        Emit(clear, codeSize);
        var prefix = (int)indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            var key = (prefix << 8) | indices[i];
            if (table.TryGetValue(key, out var code))
            {
                prefix = code;
                continue;
            }
            Emit(prefix, codeSize);
            if (next < 4096)
            {
                table[key] = next++;
                if (next > 1 << codeSize && codeSize < 12)
                    codeSize++;
            }
            else
            {
                Emit(clear, codeSize);
                table.Clear();
                codeSize = minCodeSize + 1;
                next = end + 1;
            }
            prefix = indices[i];
        }
        Emit(prefix, codeSize);
        Emit(end, codeSize);
        if (bitCount > 0)
            bytes.Add((byte)bitBuffer);

        for (var offset = 0; offset < bytes.Count; offset += 255)
        {
            var length = Math.Min(255, bytes.Count - offset);
            writer.Write((byte)length);
            for (var j = 0; j < length; j++)
                writer.Write(bytes[offset + j]);
        }
        writer.Write((byte)0);
    }
}

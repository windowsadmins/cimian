// IconService.cs - Loads and caches app icons from the Munki-style icons directory.
// Falls back to generating initials-based icons when no file is found.

using System.Collections.Concurrent;
using System.IO;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Loads icons from C:\ProgramData\ManagedInstalls\icons\ and caches them in memory.
/// Generates colored initials icons as fallback.
/// </summary>
public sealed class IconService : IIconService
{
    private static readonly string IconsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ManagedInstalls", "icons");

    private static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".ico", ".bmp"];

    // Cache: key = itemName (lowercase), value = weak reference to BitmapImage
    private readonly ConcurrentDictionary<string, WeakReference<BitmapImage>> _cache = new();

    // Fallback colors for initials icons (pleasant palette)
    private static readonly uint[] FallbackColors =
    [
        0xFF4A90D9, // blue
        0xFF7B68EE, // medium slate blue
        0xFF50C878, // emerald green
        0xFFE8703A, // orange
        0xFFCD5C5C, // indian red
        0xFF6A5ACD, // slate blue
        0xFF20B2AA, // light sea green
        0xFFDA70D6, // orchid
        0xFF708090, // slate gray
        0xFFDC143C, // crimson
    ];

    public async Task<BitmapImage> GetIconAsync(string itemName, string? iconFileName = null)
    {
        var cacheKey = itemName.ToLowerInvariant();

        // Check cache first
        if (_cache.TryGetValue(cacheKey, out var weakRef) && weakRef.TryGetTarget(out var cached))
            return cached;

        // Try to load from disk
        var bitmap = await TryLoadFromDiskAsync(itemName, iconFileName);
        bitmap ??= GenerateFallbackIcon(itemName);

        _cache[cacheKey] = new WeakReference<BitmapImage>(bitmap);
        return bitmap;
    }

    public void ClearCache() => _cache.Clear();

    private static async Task<BitmapImage?> TryLoadFromDiskAsync(string itemName, string? iconFileName)
    {
        // Build candidate filenames to try
        var candidates = new List<string>();

        // If an explicit icon filename was provided, try it first
        if (!string.IsNullOrWhiteSpace(iconFileName))
        {
            candidates.Add(iconFileName);
            // Also try without extension in case it's just a name
            var nameOnly = Path.GetFileNameWithoutExtension(iconFileName);
            foreach (var ext in SupportedExtensions)
                candidates.Add(nameOnly + ext);
        }

        // Try itemName with each supported extension
        foreach (var ext in SupportedExtensions)
            candidates.Add(itemName + ext);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.Combine(IconsDirectory, candidate);
            // Ensure the resolved path stays within the icons directory
            var resolvedPath = Path.GetFullPath(fullPath);
            if (!resolvedPath.StartsWith(IconsDirectory, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!File.Exists(resolvedPath))
                continue;

            try
            {
                var bitmap = new BitmapImage();
                using var stream = File.OpenRead(resolvedPath);
                var memStream = new MemoryStream();
                await stream.CopyToAsync(memStream);
                memStream.Position = 0;

                var ras = memStream.AsRandomAccessStream();
                await bitmap.SetSourceAsync(ras);
                return bitmap;
            }
            catch
            {
                // Corrupted file or unsupported format — skip
            }
        }

        return null;
    }

    /// <summary>
    /// Generate a simple colored fallback icon as a solid-color PNG BitmapImage.
    /// </summary>
    private static BitmapImage GenerateFallbackIcon(string itemName)
    {
        // Pick color deterministically based on name
        var hash = Math.Abs(itemName.GetHashCode(StringComparison.OrdinalIgnoreCase));
        var colorValue = FallbackColors[hash % FallbackColors.Length];

        byte a = (byte)(colorValue >> 24);
        byte r = (byte)(colorValue >> 16);
        byte g = (byte)(colorValue >> 8);
        byte b = (byte)(colorValue);

        return CreateSolidColorBitmap(r, g, b, a);
    }

    private static BitmapImage CreateSolidColorBitmap(byte r, byte g, byte b, byte a)
    {
        // Create a minimal valid 1x1 PNG, decode it, then we'll rely on the UI scaling it up.
        // A more production approach would render text, but for Phase 1, a solid color tile
        // with the item name shown below (which the card template already does) is clean.
        const int size = 64;
        var png = CreatePngBytes(size, size, r, g, b, a);

        var bitmap = new BitmapImage();
        var ms = new MemoryStream(png);
        var ras = ms.AsRandomAccessStream();
        // SetSourceAsync can't be awaited here (sync context), use synchronous path
        bitmap.SetSource(ras);
        return bitmap;
    }

    /// <summary>
    /// Create a minimal uncompressed PNG byte array of a solid color.
    /// Uses DEFLATE stored blocks (no compression) for simplicity.
    /// </summary>
    private static byte[] CreatePngBytes(int width, int height, byte r, byte g, byte b, byte a)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // PNG signature
        bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR chunk
        WriteChunk(bw, "IHDR", writer =>
        {
            writer.Write(ToBigEndian(width));
            writer.Write(ToBigEndian(height));
            writer.Write((byte)8); // bit depth
            writer.Write((byte)6); // color type: RGBA
            writer.Write((byte)0); // compression
            writer.Write((byte)0); // filter
            writer.Write((byte)0); // interlace
        });

        // IDAT chunk — raw image data wrapped in zlib/deflate
        WriteChunk(bw, "IDAT", writer =>
        {
            // Build raw scanlines: each row = filter byte (0) + RGBA pixels
            var rowBytes = 1 + width * 4;
            var rawData = new byte[height * rowBytes];
            for (int y = 0; y < height; y++)
            {
                var offset = y * rowBytes;
                rawData[offset] = 0; // no filter
                for (int x = 0; x < width; x++)
                {
                    var px = offset + 1 + x * 4;
                    rawData[px] = r;
                    rawData[px + 1] = g;
                    rawData[px + 2] = b;
                    rawData[px + 3] = a;
                }
            }

            // Wrap in zlib: 2 byte header + deflate stored blocks + 4 byte adler32
            writer.Write((byte)0x78); // CMF
            writer.Write((byte)0x01); // FLG

            // Write as stored deflate blocks (max block = 65535 bytes)
            int remaining = rawData.Length;
            int pos = 0;
            while (remaining > 0)
            {
                var blockSize = Math.Min(remaining, 65535);
                bool lastBlock = (remaining - blockSize) == 0;
                writer.Write((byte)(lastBlock ? 0x01 : 0x00));
                writer.Write((ushort)blockSize);
                writer.Write((ushort)(~blockSize & 0xFFFF));
                writer.Write(rawData, pos, blockSize);
                pos += blockSize;
                remaining -= blockSize;
            }

            // Adler32 checksum
            uint adler = Adler32(rawData);
            writer.Write(ToBigEndian((int)adler));
        });

        // IEND chunk
        WriteChunk(bw, "IEND", _ => { });

        return ms.ToArray();
    }

    private static void WriteChunk(BinaryWriter bw, string type, Action<BinaryWriter> writeData)
    {
        using var dataMs = new MemoryStream();
        using var dataBw = new BinaryWriter(dataMs);
        writeData(dataBw);
        dataBw.Flush();
        var data = dataMs.ToArray();

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        bw.Write(ToBigEndian(data.Length));
        bw.Write(typeBytes);
        bw.Write(data);

        // CRC32 over type + data
        var crcData = new byte[typeBytes.Length + data.Length];
        Buffer.BlockCopy(typeBytes, 0, crcData, 0, typeBytes.Length);
        Buffer.BlockCopy(data, 0, crcData, typeBytes.Length, data.Length);
        bw.Write(ToBigEndian((int)Crc32(crcData)));
    }

    private static byte[] ToBigEndian(int value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return bytes;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b2 = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b2 = (b2 + a) % 65521;
        }
        return (b2 << 16) | a;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var d in data)
        {
            crc ^= d;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320 * (crc & 1));
        }
        return crc ^ 0xFFFFFFFF;
    }
}

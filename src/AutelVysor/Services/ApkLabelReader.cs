using System.IO.Compression;
using System.Text;

namespace AutelVysor.Services;

/// <summary>Reads ordinary compiled Android application labels without installing a device helper.</summary>
internal static class ApkLabelReader
{
    public static string? Read(string apk)
    {
        using var zip = ZipFile.OpenRead(apk);
        byte[] Load(string name)
        {
            using var input = zip.GetEntry(name)?.Open() ?? throw new InvalidDataException($"Missing {name}");
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }
        var xml = Load("AndroidManifest.xml");
        string[] strings = [];
        uint reference = 0;
        foreach (var offset in Chunks(xml, U16(xml, 2), xml.Length))
        {
            if (U16(xml, offset) == 1) strings = StringPool(xml, offset);
            if (U16(xml, offset) != 0x102) continue;
            var node = offset + U16(xml, offset + 2);
            if (At(strings, U32(xml, node + 4)) != "application") continue;
            var start = node + U16(xml, node + 8);
            var size = U16(xml, node + 10);
            var count = U16(xml, node + 12);
            if (size < 20) throw new InvalidDataException("Invalid manifest attributes");
            for (var i = 0; i < count; i++)
            {
                var a = start + i * size;
                if (At(strings, U32(xml, a + 4)) != "label") continue;
                var type = xml[a + 15];
                if (type == 3) return At(strings, U32(xml, a + 16));
                if (type == 1) reference = U32(xml, a + 16);
            }
        }
        if (reference == 0) return null;
        var table = Load("resources.arsc");
        string[] values = [];
        var resolved = new Dictionary<uint, (byte Type, uint Data, int Score)>();
        foreach (var offset in Chunks(table, U16(table, 2), table.Length))
        {
            if (U16(table, offset) == 1) values = StringPool(table, offset);
            if (U16(table, offset) != 0x200) continue;
            var packageId = U32(table, offset + 8);
            var typeOffset = U16(table, offset + 2) >= 288 ? U32(table, offset + 284) : 0;
            foreach (var chunk in Chunks(table, offset + U16(table, offset + 2), offset + checked((int)U32(table, offset + 4))))
            {
                if (U16(table, chunk) != 0x201) continue;
                var flags = table[chunk + 9];
                var count = checked((int)U32(table, chunk + 12));
                var entries = chunk + checked((int)U32(table, chunk + 16));
                var offsets = chunk + U16(table, chunk + 2);
                // Prefer default configuration; localized strings remain a useful fallback.
                var defaultConfig = true;
                var configSize = checked((int)U32(table, chunk + 20));
                for (var p = chunk + 24; p < chunk + 20 + configSize; p++)
                    if (table[p] != 0) { defaultConfig = false; break; }
                var language = configSize >= 12 ? Encoding.ASCII.GetString(table, chunk + 28, 2).TrimEnd('\0') : "";
                var preferredLanguage = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                var score = language == preferredLanguage ? 3 : defaultConfig ? 2 : language.Length == 0 ? 1 : 0;
                for (var i = 0; i < count; i++)
                {
                    var entryIndex = (flags & 1) != 0 ? U16(table, offsets + i * 4) : i;
                    var relative = (flags & 1) != 0 ? (uint)U16(table, offsets + i * 4 + 2) * 4
                        : (flags & 2) != 0 ? (U16(table, offsets + i * 2) == ushort.MaxValue ? uint.MaxValue : (uint)U16(table, offsets + i * 2) * 4)
                        : U32(table, offsets + i * 4);
                    if (relative == uint.MaxValue) continue;
                    var entry = entries + checked((int)relative);
                    if ((U16(table, entry + 2) & 1) != 0) continue;
                    var compact = (U16(table, entry + 2) & 8) != 0;
                    var value = entry + U16(table, entry);
                    var id = (packageId << 24) | ((uint)(table[chunk + 8] + typeOffset) << 16) | (uint)entryIndex;
                    if (!resolved.TryGetValue(id, out var old) || score > old.Score)
                        resolved[id] = compact ? ((byte)(U16(table, entry + 2) >> 8), U32(table, entry + 4), score)
                            : (table[value + 3], U32(table, value + 4), score);
                }
            }
        }
        for (var depth = 0; depth < 16 && resolved.TryGetValue(reference, out var value); depth++)
        {
            if (value.Type == 3) return At(values, value.Data);
            if (value.Type != 1) break;
            reference = value.Data;
        }
        return null;
    }

    private static IEnumerable<int> Chunks(byte[] data, int start, int end)
    {
        for (var p = start; p + 8 <= end;)
        {
            var size = checked((int)U32(data, p + 4));
            if (size < 8 || size > end - p) throw new InvalidDataException("Invalid Android resource chunk");
            yield return p;
            p += size;
        }
    }

    private static string[] StringPool(byte[] data, int offset)
    {
        var count = checked((int)U32(data, offset + 8));
        if (count > data.Length / 4) throw new InvalidDataException("Invalid string pool size");
        var utf8 = (U32(data, offset + 16) & 0x100) != 0;
        var start = offset + checked((int)U32(data, offset + 20));
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var p = start + checked((int)U32(data, offset + U16(data, offset + 2) + i * 4));
            int Length()
            {
                if (utf8) { var n = data[p++]; return (n & 0x80) == 0 ? n : ((n & 0x7f) << 8) | data[p++]; }
                var v = U16(data, p); p += 2;
                if ((v & 0x8000) == 0) return v;
                var lo = U16(data, p); p += 2;
                return ((v & 0x7fff) << 16) | lo;
            }
            var length = Length();
            if (utf8) length = Length();
            result[i] = utf8 ? Encoding.UTF8.GetString(data, p, length) : Encoding.Unicode.GetString(data, p, checked(length * 2));
        }
        return result;
    }

    private static string? At(string[] values, uint i) => i < values.Length ? values[i] : null;
    private static ushort U16(byte[] data, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    private static uint U32(byte[] data, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
}

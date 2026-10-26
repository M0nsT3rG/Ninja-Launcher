using System.Buffers.Binary;
using System.Text;

namespace NinjaLauncher.Minecraft.Java.Launch;

/// <summary>
/// Adds one server to an instance's Multiplayer list (<c>servers.dat</c>, uncompressed big-endian NBT) exactly once.
/// Every existing entry and field is kept byte for byte; a marker file records that the server was offered, so a
/// player who deletes it does not get it back. An unreadable list is left untouched.
/// </summary>
public static class ServerList
{
    public const string FileName = "servers.dat";
    public const string MarkerFileName = ".ninja-listed-server";
    private const int MaxBytes = 4 * 1024 * 1024;

    /// <returns>True when the server was added now.</returns>
    public static bool AddOnce(string gameDirectory, string name, string address)
    {
        var marker = Path.Combine(gameDirectory, MarkerFileName);
        if (File.Exists(marker)) return false;
        var path = Path.Combine(gameDirectory, FileName);

        var root = new Compound();
        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaxBytes) return false;
            try { root = Read(bytes); }
            catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException) { return false; }
        }

        var servers = root.Get("servers") as ListTag;
        if (servers is null)
        {
            servers = new ListTag(TagCompound, []);
            root.Set("servers", TagList, servers);
        }
        if (servers.ElementType != TagCompound && servers.Items.Count > 0) return false; // not a server list we understand
        servers.ElementType = TagCompound;

        var ip = Utf8(address);
        var added = !servers.Items.OfType<Compound>().Any(s => s.Get("ip") is byte[] existing && existing.AsSpan().SequenceEqual(ip));
        if (added)
        {
            var entry = new Compound();
            entry.Set("name", TagString, Utf8(name));
            entry.Set("ip", TagString, ip);
            servers.Items.Insert(0, entry);
            Directory.CreateDirectory(gameDirectory);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, Write(root));
            File.Move(temp, path, overwrite: true);
        }
        File.WriteAllText(marker, "Ninja Launcher added its listed server to servers.dat once; delete this file to offer it again.\n");
        return added;
    }

    // ---- minimal NBT (only what servers.dat needs to round-trip any content) ----

    private const byte TagEnd = 0, TagByte = 1, TagShort = 2, TagInt = 3, TagLong = 4, TagFloat = 5, TagDouble = 6,
        TagByteArray = 7, TagString = 8, TagList = 9, TagCompound = 10, TagIntArray = 11, TagLongArray = 12;

    internal sealed class Compound
    {
        // Names and strings stay as their raw (modified UTF-8) bytes, so nothing is re-encoded.
        public List<(byte[] Name, byte Type, object Value)> Entries { get; } = [];

        public object? Get(string name)
        {
            var key = Utf8(name);
            return Entries.FirstOrDefault(e => e.Name.AsSpan().SequenceEqual(key)).Value;
        }

        public void Set(string name, byte type, object value)
        {
            var key = Utf8(name);
            var i = Entries.FindIndex(e => e.Name.AsSpan().SequenceEqual(key));
            if (i >= 0) Entries[i] = (key, type, value);
            else Entries.Add((key, type, value));
        }
    }

    internal sealed class ListTag(byte elementType, List<object> items)
    {
        public byte ElementType { get; set; } = elementType;
        public List<object> Items { get; } = items;
    }

    internal static Compound Read(byte[] bytes)
    {
        var pos = 0;
        if (bytes.Length < 3 || bytes[pos++] != TagCompound) throw new InvalidDataException("servers.dat does not start with a compound tag.");
        ReadString(bytes, ref pos); // root name, normally empty
        return (Compound)ReadPayload(bytes, ref pos, TagCompound, 0);
    }

    private static object ReadPayload(byte[] b, ref int pos, byte type, int depth)
    {
        if (depth > 64) throw new InvalidDataException("NBT nested too deeply.");
        switch (type)
        {
            case TagByte: return Take(b, ref pos, 1);
            case TagShort: return Take(b, ref pos, 2);
            case TagInt or TagFloat: return Take(b, ref pos, 4);
            case TagLong or TagDouble: return Take(b, ref pos, 8);
            case TagByteArray: return TakeArray(b, ref pos, 1);
            case TagIntArray: return TakeArray(b, ref pos, 4);
            case TagLongArray: return TakeArray(b, ref pos, 8);
            case TagString: return ReadString(b, ref pos);
            case TagList:
            {
                var elementType = Take(b, ref pos, 1)[0];
                var count = BinaryPrimitives.ReadInt32BigEndian(Take(b, ref pos, 4));
                if (count < 0 || count > b.Length) throw new InvalidDataException("Bad NBT list length.");
                var items = new List<object>(count);
                for (var i = 0; i < count; i++) items.Add(ReadPayload(b, ref pos, elementType, depth + 1));
                return new ListTag(elementType, items);
            }
            case TagCompound:
            {
                var c = new Compound();
                while (true)
                {
                    var t = Take(b, ref pos, 1)[0];
                    if (t == TagEnd) return c;
                    var name = ReadString(b, ref pos);
                    c.Entries.Add((name, t, ReadPayload(b, ref pos, t, depth + 1)));
                }
            }
            default: throw new InvalidDataException($"Unknown NBT tag {type}.");
        }
    }

    private static byte[] Take(byte[] b, ref int pos, int n)
    {
        if (n < 0 || pos + n > b.Length) throw new InvalidDataException("Truncated NBT.");
        var r = b.AsSpan(pos, n).ToArray();
        pos += n;
        return r;
    }

    // Length prefix is kept with the payload so arrays are written back unchanged.
    private static byte[] TakeArray(byte[] b, ref int pos, int width)
    {
        var count = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(pos, 4));
        if (count < 0 || (long)count * width > b.Length) throw new InvalidDataException("Bad NBT array length.");
        return Take(b, ref pos, 4 + count * width);
    }

    private static byte[] ReadString(byte[] b, ref int pos) => Take(b, ref pos, BinaryPrimitives.ReadUInt16BigEndian(Take(b, ref pos, 2)));

    internal static byte[] Write(Compound root)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(TagCompound);
        WriteString(ms, []);
        WritePayload(ms, TagCompound, root);
        return ms.ToArray();
    }

    private static void WritePayload(Stream s, byte type, object value)
    {
        switch (value)
        {
            case Compound c:
                foreach (var (name, t, v) in c.Entries) { s.WriteByte(t); WriteString(s, name); WritePayload(s, t, v); }
                s.WriteByte(TagEnd);
                break;
            case ListTag l:
                s.WriteByte(l.Items.Count == 0 ? TagEnd : l.ElementType);
                Span<byte> count = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(count, l.Items.Count);
                s.Write(count);
                foreach (var item in l.Items) WritePayload(s, l.ElementType, item);
                break;
            case byte[] raw when type == TagString: WriteString(s, raw); break;
            case byte[] raw: s.Write(raw); break;
            default: throw new InvalidOperationException("Unexpected NBT value.");
        }
    }

    private static void WriteString(Stream s, byte[] utf)
    {
        if (utf.Length > ushort.MaxValue) throw new InvalidDataException("NBT string too long.");
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)utf.Length);
        s.Write(len);
        s.Write(utf);
    }

    /// <summary>Java's "modified UTF-8": NUL as two bytes, characters outside the BMP as two 3-byte surrogates.</summary>
    internal static byte[] Utf8(string text)
    {
        var bytes = new List<byte>(text.Length);
        foreach (var c in text)
        {
            if (c is >= '\u0001' and <= '\u007F') bytes.Add((byte)c);
            else if (c <= '߿') { bytes.Add((byte)(0xC0 | (c >> 6))); bytes.Add((byte)(0x80 | (c & 0x3F))); }
            else { bytes.Add((byte)(0xE0 | (c >> 12))); bytes.Add((byte)(0x80 | ((c >> 6) & 0x3F))); bytes.Add((byte)(0x80 | (c & 0x3F))); }
        }
        return [.. bytes];
    }

    internal static string Text(byte[] modifiedUtf8) => Encoding.UTF8.GetString(modifiedUtf8); // for tests and diagnostics (BMP text)
}

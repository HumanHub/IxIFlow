using System.Buffers.Binary;
using System.Text;

namespace IxIFlow.Core;

/// <summary>Reads loader imports without executing package code. Unknown formats fail closed.</summary>
internal sealed record WorkflowNativeImports(string[] Dependencies, string? Identity = null)
{
    internal static WorkflowNativeImports Read(ReadOnlyMemory<byte> image)
    {
        var bytes = image.ToArray();
        try
        {
            if (bytes.AsSpan().StartsWith("\x7f"u8) && bytes.AsSpan(1, 3).SequenceEqual("ELF"u8)) return Elf(bytes);
            if (bytes.AsSpan().StartsWith("MZ"u8)) return Pe(bytes);
            if (BinaryPrimitives.ReadUInt32BigEndian(bytes) is 0xcafebabe or 0xcafebabf) return FatMach(bytes);
            return Mach(bytes);
        }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or OverflowException or InvalidDataException)
        { throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Invalid or unsupported native library import table", error); }
    }

    private static string Text(byte[] bytes, int offset, int end)
    {
        if (offset < 0 || end > bytes.Length || offset >= end) throw new InvalidDataException();
        var zero = Array.IndexOf(bytes, (byte)0, offset, end - offset);
        if (zero < 0) throw new InvalidDataException();
        return new UTF8Encoding(false, true).GetString(bytes, offset, zero - offset);
    }

    private static WorkflowNativeImports Elf(byte[] b)
    {
        var wide = b[4] == 2;
        if (b[4] is not (1 or 2) || b[5] is not (1 or 2)) throw new InvalidDataException();
        var little = b[5] == 1;
        ulong Number(int offset, int size) => size switch
        {
            2 => little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(offset)),
            4 => little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(offset)),
            _ => little ? BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(offset)) : BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(offset))
        };
        var ph = checked((int)Number(wide ? 32 : 28, wide ? 8 : 4));
        var size = checked((int)Number(wide ? 54 : 42, 2));
        var count = checked((int)Number(wide ? 56 : 44, 2));
        var loads = new List<(ulong Address, ulong Offset, ulong Length)>();
        var dynamic = 0; var length = 0;
        for (var i = 0; i < count; i++)
        {
            var p = checked(ph + i * size);
            var type = Number(p, 4);
            var offset = Number(p + (wide ? 8 : 4), wide ? 8 : 4);
            var address = Number(p + (wide ? 16 : 8), wide ? 8 : 4);
            var fileSize = Number(p + (wide ? 32 : 16), wide ? 8 : 4);
            if (type == 1) loads.Add((address, offset, fileSize));
            if (type == 2) { dynamic = checked((int)offset); length = checked((int)fileSize); }
        }
        if (dynamic == 0 || length == 0) throw new InvalidDataException();
        ulong strings = 0; var stringLength = 0; ulong? soname = null;
        var needed = new List<ulong>(); var searchPaths = new List<ulong>();
        for (var p = dynamic; p < checked(dynamic + length); p += wide ? 16 : 8)
        {
            var tag = Number(p, wide ? 8 : 4); var value = Number(p + (wide ? 8 : 4), wide ? 8 : 4);
            if (tag == 0) break;
            if (tag == 1) needed.Add(value);
            if (tag == 5) strings = value;
            if (tag == 10) stringLength = checked((int)value);
            if (tag == 14) soname = value;
            // Search directives can redirect a transitive import before our resolver sees it.
            if (tag is 15 or 29) searchPaths.Add(value);
            if (tag is 0x7ffffffd or 0x7fffffff or 0x6ffffefb or 0x6ffffefc or 0x6ffffefa)
                throw new InvalidDataException("Native filters, audit libraries and loader configuration are not allowed");
        }
        if (needed.Count == 0 && soname == null && searchPaths.Count == 0) return new([]);
        var segment = loads.First(load => strings >= load.Address && strings - load.Address < load.Length);
        var start = checked((int)(segment.Offset + strings - segment.Address));
        string Name(ulong offset) => Text(b, checked(start + (int)offset), checked(start + stringLength));
        if (searchPaths.SelectMany(offset => Name(offset).Split(':')).Any(path => path is not ("$ORIGIN" or "${ORIGIN}")))
            throw new InvalidDataException("Native search paths must refer only to the declared image directory");
        return new(needed.Select(Name).ToArray(), soname.HasValue ? Name(soname.Value) : null);
    }

    private static WorkflowNativeImports Pe(byte[] b)
    {
        int I(int p) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
        int S(int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
        var header = I(0x3c);
        if (I(header) != 0x4550) throw new InvalidDataException();
        var optional = header + 24; var wide = S(optional) == 0x20b;
        if (!wide && S(optional) != 0x10b) throw new InvalidDataException();
        var sections = optional + S(header + 20);
        int Map(int address)
        {
            for (var i = 0; i < S(header + 6); i++)
            {
                var p = sections + 40 * i; var va = I(p + 12);
                if (address >= va && address - va < I(p + 16)) return checked(I(p + 20) + address - va);
            }
            throw new InvalidDataException();
        }
        var directory = optional + (wide ? 112 : 96);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Imports(int index, int stride, int nameOffset)
        {
            var rva = I(directory + index * 8);
            if (rva == 0) return;
            var end = checked(Map(rva) + I(directory + index * 8 + 4));
            for (var p = Map(rva); p + stride <= end && I(p + nameOffset) != 0; p += stride)
            {
                if (index == 13 && (I(p) & 1) == 0) throw new InvalidDataException();
                result.Add(Text(b, Map(I(p + nameOffset)), b.Length));
            }
        }
        Imports(1, 20, 12); Imports(13, 32, 4);
        // Export forwarders also cause loader imports.
        var exportRva = I(directory); var exportSize = I(directory + 4);
        if (exportRva != 0)
        {
            var export = Map(exportRva); var functions = Map(I(export + 28));
            for (var i = 0; i < I(export + 20); i++)
            {
                var rva = I(functions + i * 4);
                if (rva >= exportRva && rva - exportRva < exportSize)
                {
                    var forward = Text(b, Map(rva), b.Length);
                    result.Add(forward[..forward.LastIndexOf('.')] + ".dll");
                }
            }
        }
        return new(result.ToArray());
    }

    private static WorkflowNativeImports Mach(byte[] b)
    {
        uint I(int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
        var magic = I(0);
        if (magic is not (0xfeedface or 0xfeedfacf)) throw new InvalidDataException();
        var names = new List<string>(); string? identity = null;
        var p = magic == 0xfeedfacf ? 32 : 28;
        for (var i = 0; i < I(16); i++)
        {
            var command = I(p); var size = checked((int)I(p + 4));
            if (size < 8) throw new InvalidDataException();
            if (command is 0xc or 0xd or 0x80000018 or 0x8000001f or 0x20 or 0x80000023)
            {
                var name = Text(b, checked(p + (int)I(p + 8)), checked(p + size));
                if (command == 0xd) identity = name; else names.Add(name);
            }
            if (command is 0x8000001c or 0x27) throw new InvalidDataException("Native rpaths and loader environment overrides are not allowed");
            p = checked(p + size);
        }
        return new(names.ToArray(), identity);
    }

    private static WorkflowNativeImports FatMach(byte[] b)
    {
        var wide = BinaryPrimitives.ReadUInt32BigEndian(b) == 0xcafebabf;
        var count = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4));
        var tables = new List<WorkflowNativeImports>();
        for (var i = 0; i < count; i++)
        {
            var p = checked(8 + i * (wide ? 32 : 20));
            var offset = checked((int)(wide ? BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p + 8)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p + 8))));
            var length = checked((int)(wide ? BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p + 16)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p + 12))));
            tables.Add(Mach(b.AsSpan(offset, length).ToArray()));
        }
        if (tables.Count == 0 || tables.Select(table => table.Identity).Distinct().Count() != 1) throw new InvalidDataException();
        return new(tables.SelectMany(table => table.Dependencies).Distinct(StringComparer.Ordinal).ToArray(), tables[0].Identity);
    }
}

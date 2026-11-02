using System.Buffers.Binary;
using System.Text;

namespace RpgLauncher;

/// <summary>The parts of a 32-bit Windows exe that patching needs: image base, sections and imports.</summary>
internal sealed class PeImage
{
    public sealed record Section(string Name, uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize, uint Characteristics)
    {
        public bool IsCode => (Characteristics & 0x20) != 0 || (Characteristics & 0x20000000) != 0;
    }

    public byte[] Bytes { get; }
    public uint ImageBase { get; private set; }

    /// <summary>True when the image may be loaded elsewhere than <see cref="ImageBase"/> (ASLR).</summary>
    public bool Relocatable { get; private set; }

    public List<Section> Sections { get; } = new();

    // (dll, function) → RVA of its import address table slot.
    private readonly Dictionary<(string Dll, string Function), uint> _imports = new();

    private PeImage(byte[] bytes) => Bytes = bytes;

    /// <summary>Reads a 32-bit PE file; null when it isn't one.</summary>
    public static PeImage? Read(byte[] bytes)
    {
        try
        {
            var image = new PeImage(bytes);
            return image.Parse() ? image : null;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(offset, 4));
    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(offset, 2));

    private bool Parse()
    {
        if (Bytes.Length < 0x40 || Bytes[0] != 'M' || Bytes[1] != 'Z') return false;
        int pe = (int)U32(0x3C);
        if (pe <= 0 || pe + 0xF8 > Bytes.Length || U32(pe) != 0x00004550) return false;
        int sectionCount = U16(pe + 6);
        int optionalSize = U16(pe + 20);
        int optional = pe + 24;
        if (U16(optional) != 0x10B) return false; // PE32 only: RPG_RT.exe is 32-bit
        ImageBase = U32(optional + 28);
        Relocatable = (U16(optional + 70) & 0x40) != 0;
        uint importRva = U32(optional + 104);

        int table = optional + optionalSize;
        for (int i = 0; i < sectionCount; i++)
        {
            int s = table + i * 40;
            string name = Encoding.ASCII.GetString(Bytes, s, 8).TrimEnd('\0');
            Sections.Add(new Section(name, U32(s + 12), U32(s + 8), U32(s + 20), U32(s + 16), U32(s + 36)));
        }
        if (importRva != 0) ReadImports(importRva);
        return true;
    }

    /// <summary>The file offset of an RVA, or -1 when it isn't backed by the file.</summary>
    public int Offset(uint rva)
    {
        foreach (var section in Sections)
        {
            uint size = Math.Max(section.VirtualSize, section.RawSize);
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + size && rva - section.VirtualAddress < section.RawSize)
                return (int)(section.RawOffset + rva - section.VirtualAddress);
        }
        return -1;
    }

    private string? AsciiAt(uint rva)
    {
        int offset = Offset(rva);
        if (offset < 0) return null;
        int end = Array.IndexOf(Bytes, (byte)0, offset);
        return end < 0 ? null : Encoding.ASCII.GetString(Bytes, offset, end - offset);
    }

    private void ReadImports(uint rva)
    {
        for (int descriptor = Offset(rva); descriptor >= 0 && descriptor + 20 <= Bytes.Length; descriptor += 20)
        {
            uint lookup = U32(descriptor), nameRva = U32(descriptor + 12), iat = U32(descriptor + 16);
            if (nameRva == 0 && iat == 0) break;
            string dll = (AsciiAt(nameRva) ?? "").ToLowerInvariant();
            // Delphi's linker leaves the lookup table out; the address table holds the names until loading.
            int thunks = Offset(lookup != 0 ? lookup : iat);
            for (int i = 0; thunks >= 0 && thunks + i * 4 + 4 <= Bytes.Length; i++)
            {
                uint thunk = U32(thunks + i * 4);
                if (thunk == 0) break;
                if ((thunk & 0x80000000) != 0) continue; // by ordinal
                string? function = AsciiAt(thunk + 2);
                if (function is not null) _imports[(dll, function)] = (uint)(iat + i * 4);
            }
        }
    }

    /// <summary>The RVA of an import's address-table slot, or 0 when the exe doesn't import it.</summary>
    public uint ImportSlot(string dll, string function) =>
        _imports.TryGetValue((dll.ToLowerInvariant(), function), out uint slot) ? slot : 0;
}

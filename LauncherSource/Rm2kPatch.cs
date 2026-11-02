using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RpgLauncher;

/// <summary>
/// MoshimoLoader's メモスト対策 without its DLL. RPG_RT's MIDI buffer cleanup cuts the handle GlobalHandle
/// returns to 16 bits (movzx eax,ax) before GlobalUnlock and GlobalFree, so on current Windows every buffer
/// leaks until the process runs out of moveable memory and stops with a memory stream error. The 3-byte
/// movzx is overwritten with NOPs in the suspended process; the exe on disk is not touched.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Rm2kPatch
{
    private enum SiteState { NotPe32, NoImport, NoSite, AlreadyFixed, Found, Ambiguous }

    private const uint PageExecuteReadWrite = 0x40;

    /// <summary>
    /// Finds the call site in the exe: the call to kernel32!GlobalHandle (through Delphi's jmp thunk) that is
    /// followed by 0F B7 C0 8B (movzx eax,ax; mov). Exactly one site is required; RVA of the movzx.
    /// </summary>
    private static (SiteState State, uint Rva) FindSite(byte[] bytes)
    {
        if (PeImage.Read(bytes) is not { } image) return (SiteState.NotPe32, 0);
        uint slot = image.ImportSlot("kernel32.dll", "GlobalHandle");
        if (slot == 0) return (SiteState.NoImport, 0);
        uint slotVa = image.ImageBase + slot;

        // jmp [slot] thunks and direct call [slot]s in code sections.
        var thunks = new HashSet<uint>();
        var returns = new List<uint>();
        foreach (var section in image.Sections.Where(s => s.IsCode))
        {
            for (uint offset = 0; offset + 6 <= section.RawSize && section.RawOffset + offset + 6 <= bytes.Length; offset++)
            {
                int at = (int)(section.RawOffset + offset);
                if (bytes[at] != 0xFF || (bytes[at + 1] != 0x25 && bytes[at + 1] != 0x15) || BitConverter.ToUInt32(bytes, at + 2) != slotVa)
                    continue;
                if (bytes[at + 1] == 0x25) thunks.Add(section.VirtualAddress + offset);
                else returns.Add(section.VirtualAddress + offset + 6);
            }
        }
        // call rel32 to a thunk: how Delphi calls imports.
        foreach (var section in image.Sections.Where(s => s.IsCode))
        {
            for (uint offset = 0; offset + 5 <= section.RawSize && section.RawOffset + offset + 5 <= bytes.Length; offset++)
            {
                int at = (int)(section.RawOffset + offset);
                if (bytes[at] != 0xE8) continue;
                uint target = (uint)(section.VirtualAddress + offset + 5 + BitConverter.ToInt32(bytes, at + 1));
                if (thunks.Contains(target)) returns.Add(section.VirtualAddress + offset + 5);
            }
        }

        var sites = new List<uint>();
        bool fixedAlready = false;
        foreach (uint rva in returns)
        {
            int at = image.Offset(rva);
            if (at < 0 || at + 4 > bytes.Length || bytes[at + 3] != 0x8B) continue;
            if (bytes[at] == 0x0F && bytes[at + 1] == 0xB7 && bytes[at + 2] == 0xC0) sites.Add(rva);
            else if (bytes[at] == 0x90 && bytes[at + 1] == 0x90 && bytes[at + 2] == 0x90) fixedAlready = true;
        }
        return sites.Count switch
        {
            1 => (SiteState.Found, sites[0]),
            > 1 => (SiteState.Ambiguous, 0),
            _ => (fixedAlready ? SiteState.AlreadyFixed : SiteState.NoSite, 0)
        };
    }

    /// <summary>Patches the suspended process started from <paramref name="exe"/>; returns what was done, for the log.</summary>
    public static string Apply(IntPtr process, string exe)
    {
        var (state, rva) = FindSite(File.ReadAllBytes(exe));
        switch (state)
        {
            case SiteState.AlreadyFixed: return "the exe is already fixed";
            case SiteState.NoSite: return "no site (a patched or Maniacs build)";
            case SiteState.NoImport: return "GlobalHandle isn't imported";
            case SiteState.Ambiguous: return "several candidate sites; left alone";
            case SiteState.NotPe32: return "not a 32-bit exe";
        }

        IntPtr site = ImageBase(process) + (int)rva;
        var current = new byte[4];
        if (!ReadProcessMemory(process, site, current, current.Length, out _) ||
            current[0] != 0x0F || current[1] != 0xB7 || current[2] != 0xC0 || current[3] != 0x8B)
            return "the bytes in memory differ from the exe; left alone";

        if (!VirtualProtectEx(process, site, 3, PageExecuteReadWrite, out uint old)) throw new Win32Exception();
        bool written = WriteProcessMemory(process, site, new byte[] { 0x90, 0x90, 0x90 }, 3, out _);
        VirtualProtectEx(process, site, 3, old, out _);
        FlushInstructionCache(process, site, 3);
        if (!written) throw new Win32Exception();
        return $"patched at RVA 0x{rva:X}";
    }

    /// <summary>
    /// The exe's base address from the process environment block. From a 64-bit launcher this is the native PEB of
    /// the 32-bit (WOW64) game, whose ImageBaseAddress (+0x10) is the 32-bit exe's base; from a 32-bit one, +0x08.
    /// </summary>
    private static IntPtr ImageBase(IntPtr process)
    {
        var info = new ProcessBasicInformation();
        int status = NtQueryInformationProcess(process, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
        if (status != 0) throw new Win32Exception($"NtQueryInformationProcess failed (0x{status:X8})");
        var pointer = new byte[IntPtr.Size];
        if (!ReadProcessMemory(process, info.PebBaseAddress + (IntPtr.Size == 8 ? 0x10 : 0x08), pointer, pointer.Length, out _))
            throw new Win32Exception();
        return IntPtr.Size == 8 ? (IntPtr)BitConverter.ToInt64(pointer) : BitConverter.ToInt32(pointer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr process, IntPtr address, nuint size, uint protect, out uint old);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, nuint size);
}

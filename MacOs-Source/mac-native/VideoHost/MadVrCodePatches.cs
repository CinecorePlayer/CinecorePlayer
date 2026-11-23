#nullable enable
using System;
using System.Runtime.InteropServices;

namespace CinecoreVideoHost;

/// <summary>
/// Cicli caldi di madVR riscritti (codice x86 equivalente, piu' veloce sotto l'emulazione di FEX). Ogni correzione
/// controlla i byte originali della versione provata (madVR 0.92.17) e non tocca niente se sono diversi.
/// CINECORE_MADVR_PATCH=0 le spegne tutte.
///
/// 1. madVR64.ax+0x114f90..0x114fbe: impacchettamento della crominanza UV a 16 bit nella texture RGBA16 di caricamento,
///    un pixel per giro con il limite ricalcolato dalla memoria (2 milioni di giri a fotogramma 4K, codice scalare).
///    Sostituito da un ciclo SSE a 4 pixel per giro: stessi byte scritti, stessi r8/r9/r10 all'uscita, usa solo
///    eax/ecx/edx/xmm0-2 (la funzione non usa registri SSE). Verificato il 23/11/2026 contro il ciclo originale
///    sotto Wine/FEX su 906 casi (0 differenze): 4,18 -> 0,83 ms per fotogramma di crominanza 1920x1080.
///    Sorgente: mac-native/VideoHost/patches/madvr-chroma-pack.s
/// </summary>
internal static unsafe class MadVrCodePatches
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32")] private static extern IntPtr VirtualAlloc(IntPtr addr, nuint size, uint type, uint protect);
    [DllImport("kernel32")] private static extern bool VirtualProtect(IntPtr addr, nuint size, uint prot, out uint old);
    [DllImport("kernel32")] private static extern bool FlushInstructionCache(IntPtr process, IntPtr addr, nuint size);
    [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();

    private const int ChromaRva = 0x114f90, ChromaLength = 0x2e;
    private static readonly byte[] ChromaOriginal = Convert.FromHexString(
        "410FB74102410FB7094183C201C1E0104983C1044983C0080BC1418940F8458960FC8B06992BC2D1F8443BD07CD2");
    // cave.s assemblato (x86_64-w64-mingw32-as): termina con "jmp *0(%rip)" + 8 byte d'indirizzo di ritorno (offset 0x75).
    private static readonly byte[] ChromaCave = Convert.FromHexString(
        "8B069929D0D1F889C14429D183F9017D05B90100000089CAC1EA02" +
        "66410F6ED4660F70D20085D27428F3410F6F01660F6FC8660F62C2660F6ACAF3410F7F00F3410F7F4810" +
        "4983C1104983C020FFCA75D84101CA83E1037416418B0141890045896004" +
        "4983C1044983C008FFC975EAFF2500000000");
    private const int ChromaReturnOffset = 0x75;

    internal static void Install()
    {
        if (Environment.GetEnvironmentVariable("CINECORE_MADVR_PATCH") == "0") return;
        IntPtr mod = GetModuleHandleW("madVR64.ax");
        if (mod == IntPtr.Zero) return;
        try
        {
            byte* target = (byte*)mod + ChromaRva;
            if (!new ReadOnlySpan<byte>(target, ChromaLength).SequenceEqual(ChromaOriginal))
            {
                Out.Line("info madVR: crominanza non corretta (byte diversi dalla 0.92.17)");
                return;
            }
            byte* cave = (byte*)VirtualAlloc(IntPtr.Zero, 4096, 0x3000 /*COMMIT|RESERVE*/, 0x40 /*EXECUTE_READWRITE*/);
            if (cave == null) return;
            ChromaCave.AsSpan().CopyTo(new Span<byte>(cave, ChromaCave.Length));
            *(ulong*)(cave + ChromaReturnOffset) = (ulong)(target + ChromaLength);
            VirtualProtect((IntPtr)cave, 4096, 0x20 /*EXECUTE_READ*/, out _);
            FlushInstructionCache(GetCurrentProcess(), (IntPtr)cave, 4096);

            // Al posto del ciclo: jmp *0(%rip) verso il nostro, poi int3 fino alla fine del tratto sostituito.
            if (!VirtualProtect((IntPtr)target, ChromaLength, 0x40, out uint old)) return;
            var jump = new Span<byte>(target, ChromaLength);
            jump.Fill(0xCC);
            jump[0] = 0xFF; jump[1] = 0x25; jump[2] = jump[3] = jump[4] = jump[5] = 0;
            *(ulong*)(target + 6) = (ulong)cave;
            VirtualProtect((IntPtr)target, ChromaLength, old, out _);
            FlushInstructionCache(GetCurrentProcess(), (IntPtr)target, ChromaLength);
            Out.Line("info madVR: impacchettamento crominanza sostituito (SSE)");
        }
        catch (Exception ex) { Out.Line("info madVR: correzione crominanza non applicata: " + ex.Message); }
    }
}

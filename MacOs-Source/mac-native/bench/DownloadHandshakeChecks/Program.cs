using System.Runtime.InteropServices;

// Regression: a destination armed before the graphics command has run must never be reported as ready.
// Runs against the real Wine DLL, without creating a GPU context or a playback window.
IntPtr library = NativeLibrary.Load("wined3d.dll");
var expect = Marshal.GetDelegateForFunctionPointer<Status>(NativeLibrary.GetExport(library, "cinecore_download_expect"));
var status = Marshal.GetDelegateForFunctionPointer<Status>(NativeLibrary.GetExport(library, "cinecore_download_status"));
var wait = Marshal.GetDelegateForFunctionPointer<Wait>(NativeLibrary.GetExport(library, "cinecore_download_wait"));
Parallel.For(0, 32, _ =>
{
    IntPtr memory = Marshal.AllocHGlobal(64);
    try
    {
        for (int i = 0; i < 10; i++)
        {
            if (expect(memory) != 1 || status(memory) != 0 || wait(memory, 0) != 0)
                throw new InvalidOperationException("Download reported ready before the graphics command was submitted");
        }
    }
    finally { Marshal.FreeHGlobal(memory); }
});
Console.WriteLine("PASS: 320 downloads stay pending until their own graphics command completes");

[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Status(IntPtr memory);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Wait(IntPtr memory, uint timeout);

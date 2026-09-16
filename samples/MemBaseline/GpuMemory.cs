using System;
using System.Runtime.InteropServices;

namespace MemBaseline;

/// <summary>
/// This process's video-memory usage, summed over every DXGI adapter (IDXGIAdapter3::QueryVideoMemoryInfo,
/// which reports the CALLING process only). Private bytes cannot see textures on a discrete GPU, and a
/// decoded CanvasBitmap is exactly such a texture — so image memory is invisible without this.
///   Local    = the adapter's own memory (dedicated VRAM; on an integrated GPU, its shared system pool)
///   NonLocal = system memory the adapter pages into (discrete GPUs only, normally)
/// Same projection-free vtable style as the library's WindowsPdfPrinter.
/// </summary>
internal static unsafe class GpuMemory
{
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIAdapter3 = new("645967A4-1392-4310-A798-8053CE3E93FD");
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoMemoryInfo { public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation; }

    /// <summary>Per adapter with any usage: "Name local/nonLocal MB". Reported separately, not summed — on a
    /// hybrid laptop the editor may render on either GPU, and which one it is changes what the number means.
    /// CurrentUsage is RESIDENT memory: the OS may evict textures nobody draws, so a falling number here with
    /// flat Private bytes is eviction, not release.</summary>
    public static string QueryText()
    {
        var parts = new System.Collections.Generic.List<string>();
        ForEachAdapter((adapter3, name) =>
        {
            VideoMemoryInfo info;
            ulong local = QueryInfo(adapter3, 0, &info) >= 0 ? info.CurrentUsage : 0;
            ulong nonLocal = QueryInfo(adapter3, 1, &info) >= 0 ? info.CurrentUsage : 0;
            if (local + nonLocal == 0) return;
            string shortName = name.Split(' ') is { Length: > 1 } w && w[0] is "NVIDIA" or "AMD" or "Intel(R)" ? w[0].TrimEnd("(R)".ToCharArray()) : name;
            parts.Add($"{shortName} {local / 1048576.0:N0}/{nonLocal / 1048576.0:N0}");
        });
        return string.Join(", ", parts);
    }

    /// <summary>Adapter names, for the log header (which GPU the numbers came from matters: a UMA
    /// integrated GPU reports textures as Local, a discrete one too but in real VRAM).</summary>
    public static string Describe()
    {
        var names = new System.Collections.Generic.List<string>();
        ForEachAdapter((_, name) => names.Add(name));
        return string.Join(" | ", names);
    }

    private static int QueryInfo(nint adapter3, int segmentGroup, VideoMemoryInfo* info)
        => ((delegate* unmanaged[Stdcall]<nint, uint, int, VideoMemoryInfo*, int>)Vtbl(adapter3)[14])(
            adapter3, 0, segmentGroup, info); // IDXGIAdapter3::QueryVideoMemoryInfo(node 0, group, out)

    private static void ForEachAdapter(Action<nint, string> visit)
    {
        Guid iid = IID_IDXGIFactory1;
        nint factory = 0;
        if (CreateDXGIFactory1(&iid, &factory) < 0) return;
        try
        {
            // DXGI_ADAPTER_DESC1 starts with WCHAR Description[128]; the rest is ignored here.
            byte* desc = stackalloc byte[512];
            for (uint i = 0; ; i++)
            {
                nint adapter = 0;
                int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl(factory)[12])(factory, i, &adapter); // EnumAdapters1
                if (hr == DXGI_ERROR_NOT_FOUND || hr < 0) break;
                try
                {
                    new Span<byte>(desc, 512).Clear();
                    string name = ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Vtbl(adapter)[10])(adapter, desc) >= 0 // GetDesc1
                        ? new string((char*)desc) : "?";
                    Guid a3 = IID_IDXGIAdapter3;
                    nint adapter3 = 0;
                    if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtbl(adapter)[0])(adapter, &a3, &adapter3) < 0) continue;
                    try { visit(adapter3, name); }
                    finally { Release(adapter3); }
                }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
    }

    private static nint* Vtbl(nint unknown) => *(nint**)unknown;

    private static void Release(nint unknown)
    {
        if (unknown != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(unknown)[2])(unknown);
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(Guid* riid, nint* factory);
}

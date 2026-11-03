using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static VIvian.Infrastructure.Processes.NativeMethods;

namespace VIvian.Infrastructure.Processes;

/// <summary>Owns the native STARTUPINFOEX attribute list and its handle array.</summary>
[SupportedOSPlatform("windows")]
internal sealed class InheritedHandleList : IDisposable
{
    private IntPtr _list;
    private IntPtr _handles;
    private bool _initialized;

    public IntPtr Pointer => _list;

    public InheritedHandleList(SafeFileHandle input, SafeFileHandle output)
    {
        try
        {
            IntPtr bytes = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            if (bytes == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Attribute list size failed");
            _list = Marshal.AllocHGlobal(bytes);
            if (!InitializeProcThreadAttributeList(_list, 1, 0, ref bytes))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Attribute list initialization failed");
            _initialized = true;

            _handles = Marshal.AllocHGlobal(2 * IntPtr.Size);
            Marshal.WriteIntPtr(_handles, 0, input.DangerousGetHandle());
            Marshal.WriteIntPtr(_handles, IntPtr.Size, output.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(_list, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                    _handles, (IntPtr)(2 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Handle list update failed");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_initialized) { DeleteProcThreadAttributeList(_list); _initialized = false; }
        if (_handles != IntPtr.Zero) { Marshal.FreeHGlobal(_handles); _handles = IntPtr.Zero; }
        if (_list != IntPtr.Zero) { Marshal.FreeHGlobal(_list); _list = IntPtr.Zero; }
    }
}

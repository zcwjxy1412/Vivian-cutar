using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VIvian.Infrastructure.Engines;

namespace VIvian.Infrastructure.Engines.BetterGi;

internal static class GenshinGameExecutable
{
    private const string Publisher = "miHoYo Co.,Ltd.";

    public static void Verify(string path, FileStream file)
    {
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed ||
            ReadOnlyFileAccess.FindReparsePoint(path) is not null ||
            !ReadOnlyFileAccess.HandlePathMatches(file.SafeFileHandle, path) ||
            ReadOnlyFileAccess.LinkCount(file.SafeFileHandle) != 1)
            throw new InvalidOperationException("无法核对原神客户端的本机实际文件。");

        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var info = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path, File = file.SafeFileHandle.DangerousGetHandle() };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(info, pointer, false);
        try
        {
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(), UIChoice = 2, UnionChoice = 1,
                File = pointer, StateAction = 1, ProviderFlags = 0x1000, // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            try
            {
                if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0 || !HasExpectedSigner(data.StateData))
                    throw Untrusted();
            }
            finally
            {
                data.StateAction = 2;
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
        }
        catch (CryptographicException) { throw Untrusted(); }
        finally
        {
            Marshal.DestroyStructure<TrustFile>(pointer);
            Marshal.FreeHGlobal(pointer);
            GC.KeepAlive(file);
        }
    }

    private static bool HasExpectedSigner(IntPtr state)
    {
        var provider = WTHelperProvDataFromStateData(state);
        if (provider == IntPtr.Zero) return false;
        var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == IntPtr.Zero) return false;
        var first = WTHelperGetProvCertFromChain(signer, 0);
        if (first == IntPtr.Zero) return false;
        var pointer = Marshal.PtrToStructure<ProviderCertificate>(first).Context;
        if (pointer == IntPtr.Zero) return false;
        var context = Marshal.PtrToStructure<CertificateContext>(pointer);
        if (context.Encoded == IntPtr.Zero || context.Length is 0 or > 65536) return false;
        var encoded = new byte[context.Length];
        Marshal.Copy(context.Encoded, encoded, 0, encoded.Length);
        using var certificate = X509CertificateLoader.LoadCertificate(encoded);
        return certificate.GetNameInfo(X509NameType.SimpleName, false) == Publisher;
    }

    private static InvalidOperationException Untrusted() =>
        new("Windows 未能验证 YuanShen.exe 的米哈游签名，自动启动已停止。");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr File, KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr Policy, Sip;
        public uint UIChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData, Url;
        public uint ProviderFlags, UIContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate { public uint Size; public IntPtr Context; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext { public uint EncodingType; public IntPtr Encoded; public uint Length; }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint index, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
}

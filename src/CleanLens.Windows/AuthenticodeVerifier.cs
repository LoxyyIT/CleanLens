using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CleanLens.Windows;

public sealed record AuthenticodeResult(bool IsTrusted, string Publisher, string Status);

public sealed class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyAction = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint CacheOnlyUrlRetrieval = 0x00001000;

    public AuthenticodeResult Verify(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return new(false, string.Empty, "Unavailable");
        var filePath = Marshal.StringToCoTaskMemUni(path);
        var fileInfo = new WinTrustFileInfo { StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = filePath };
        var fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
        var trustData = new WinTrustData { StructSize = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, UnionChoice = 1, FileInfo = fileInfoPtr, ProviderFlags = CacheOnlyUrlRetrieval };
        var trustDataPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
        Marshal.StructureToPtr(trustData, trustDataPtr, false);
        try
        {
            var action = GenericVerifyAction;
            var status = WinVerifyTrust(IntPtr.Zero, ref action, trustDataPtr);
            var publisher = ReadPublisher(path);
            return status == 0
                ? new(true, publisher, "Trusted")
                : new(false, publisher, status == unchecked((int)0x800B0100) ? "Unsigned" : $"Untrusted (0x{status:X8})");
        }
        catch (Exception ex) when (ex is CryptographicException or System.ComponentModel.Win32Exception or DllNotFoundException)
        {
            return new(false, string.Empty, "Unavailable");
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustData>(trustDataPtr);
            Marshal.FreeCoTaskMem(trustDataPtr);
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPtr);
            Marshal.FreeCoTaskMem(fileInfoPtr);
            Marshal.FreeCoTaskMem(filePath);
        }
    }

    private static string ReadPublisher(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch (CryptographicException) { return string.Empty; }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr windowHandle, ref Guid actionIdentifier, IntPtr trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}

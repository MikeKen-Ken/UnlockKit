using System.Runtime.InteropServices;

namespace UnlockKit.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct SystemHandleEntry
{
    public IntPtr Object;
    public UIntPtr ProcessId;
    public UIntPtr HandleValue;
    public uint GrantedAccess;
    public ushort CreatorBackTraceIndex;
    public ushort ObjectTypeIndex;
    public uint HandleAttributes;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UnicodeString
{
    public ushort Length;
    public ushort MaximumLength;
    public IntPtr Buffer;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IoStatusBlock
{
    public IntPtr Status;
    public UIntPtr Information;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FileDispositionInformationEx
{
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessProtectionInformation
{
    public byte Level;
    public byte Type;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ByHandleFileInformation
{
    public uint FileAttributes;
    public uint CreationTimeLow;
    public uint CreationTimeHigh;
    public uint LastAccessTimeLow;
    public uint LastAccessTimeHigh;
    public uint LastWriteTimeLow;
    public uint LastWriteTimeHigh;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}

internal static class NtStatus
{
    public const uint Success = 0x00000000;
    public const uint InfoLengthMismatch = 0xC0000004;
    public const uint BufferTooSmall = 0xC0000023;
    public const uint BufferOverflow = 0x80000005;
    public const uint AccessDenied = 0xC0000022;
    public const uint SharingViolation = 0xC0000043;
    public const uint CannotDelete = 0xC0000121;
}

/// <summary>SQL-free interop surface for ntdll + kernel32 primitives this tool needs.</summary>
internal static class Native
{
    // ---- informational classes ----
    public const int SystemExtendedHandleInformation = 64;
    public const int ProcessProtectionInformationClass = 61;
    public const int ObjectNameInformation = 1;
    public const int FileDispositionInformationExClass = 64;

    // ---- FILE_DISPOSITION_INFORMATION_EX flags ----
    public const uint FileDispositionFlagDelete = 0x00000001;
    public const uint FileDispositionFlagPosixSemantics = 0x00000002;
    public const uint FileDispositionFlagForceImageSectionCheck = 0x00000004;
    public const uint FileDispositionFlagIgnoreReadonlyAttribute = 0x00000010;

    // ---- access rights ----
    public const uint ProcessTerminate = 0x0001;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint ProcessDupHandle = 0x0040;
    public const uint ProcessSynchronize = 0x00100000;
    public const uint DuplicateCloseSource = 0x0001;
    public const uint DuplicateSameAccess = 0x0002;
    public const uint WaitFailed = 0xFFFFFFFF;

    public const uint Delete = 0x00010000;
    public const uint Synchronize = 0x00100000;

    public const uint FileShareRead = 0x1;
    public const uint FileShareWrite = 0x2;
    public const uint FileShareDelete = 0x4;

    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;

    public const uint MoveFileDelayUntilReboot = 0x4;

    public const uint TokenAdjustPrivileges = 0x20;
    public const uint TokenQuery = 0x08;
    public const uint SePrivilegeEnabled = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("ntdll.dll")]
    public static extern uint NtQuerySystemInformation(
        int infoClass, IntPtr buffer, int bufferLength, out int returnLength);

    [DllImport("ntdll.dll")]
    public static extern uint NtQueryObject(
        IntPtr handle, int infoClass, IntPtr buffer, int bufferLength, out int returnLength);

    [DllImport("ntdll.dll")]
    public static extern uint NtQueryInformationProcess(
        IntPtr process, int infoClass, ref ProcessProtectionInformation info,
        int infoLength, out int returnLength);

    [DllImport("ntdll.dll")]
    public static extern uint NtSetInformationFile(
        IntPtr file, out IoStatusBlock ioStatus, ref FileDispositionInformationEx info,
        int infoLength, int infoClass);

    [DllImport("ntdll.dll")]
    public static extern int NtClose(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(
        string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DuplicateHandle(
        IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        out IntPtr targetHandle, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool MoveFileExW(string existing, string? newName, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int QueryDosDeviceW(string deviceName, char[] targetPath, int maxLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(
        IntPtr process, uint flags, char[] exeName, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(
        IntPtr token, bool disableAll, ref TokenPrivileges newState,
        int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(
        IntPtr handle, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetFinalPathNameByHandleW(
        IntPtr handle, char[] buffer, int length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentProcess();
}

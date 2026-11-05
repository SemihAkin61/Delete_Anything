using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeleteAnything.Core.Native;

/// <summary>Windows API tanımları (P/Invoke).</summary>
internal static class NativeMethods
{
    // ---- Hata kodları ----
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_LOCK_VIOLATION = 33;
    public const int ERROR_DIR_NOT_EMPTY = 145;
    public const int ERROR_MORE_DATA = 234;

    // ---- Dosya sabitleri ----
    public const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint DELETE = 0x00010000;
    public const uint FILE_SHARE_ALL = 0x7; // READ | WRITE | DELETE
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    public const int FileDispositionInfo = 4;
    public const int FileDispositionInfoEx = 21;
    public const uint FILE_DISPOSITION_FLAG_DELETE = 0x1;
    public const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2;
    public const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;

    public const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_DISPOSITION_INFO_EX { public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_DISPOSITION_INFO { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteFileW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveDirectoryW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFileAttributesW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFileAttributesW(string path, uint attributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveFileExW(string existing, string? newName, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass,
        ref FILE_DISPOSITION_INFO_EX info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass,
        ref FILE_DISPOSITION_INFO info, uint size);

    // ---- Restart Manager ----
    public const int CCH_RM_SESSION_KEY = 32;
    public const int CCH_RM_MAX_APP_NAME = 255;
    public const int CCH_RM_MAX_SVC_NAME = 63;

    [StructLayout(LayoutKind.Sequential)]
    public struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmStartSession(out uint sessionHandle, int flags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmRegisterResources(uint sessionHandle, uint nFiles, string[] files,
        uint nApplications, RM_UNIQUE_PROCESS[]? applications, uint nServices, string[]? services);

    [DllImport("rstrtmgr.dll")]
    public static extern int RmGetList(uint sessionHandle, out uint procInfoNeeded, ref uint procInfo,
        [In, Out] RM_PROCESS_INFO[]? affectedApps, ref uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    public static extern int RmEndSession(uint sessionHandle);

    // ---- Pencere işlemleri (programı düzgün kapatmak için) ----
    public const uint WM_CLOSE = 0x0010;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- Sistem handle listesi (Process Explorer / Sysinternals Handle ile aynı yöntem) ----
    public const int SystemExtendedHandleInformation = 64;
    public const uint STATUS_SUCCESS = 0;
    public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [DllImport("ntdll.dll")]
    public static extern uint NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    public const uint PROCESS_DUP_HANDLE = 0x0040;
    public const uint DUPLICATE_CLOSE_SOURCE = 0x1;
    public const uint DUPLICATE_SAME_ACCESS = 0x2;
    public const uint FILE_TYPE_DISK = 0x0001;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DuplicateHandle(SafeProcessHandle sourceProcess, IntPtr sourceHandle,
        IntPtr targetProcess, out IntPtr targetHandle, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFinalPathNameByHandleW(IntPtr handle, [Out] char[] buffer, uint length, uint flags);

    // ---- Yetki / güvenlik ----
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x2;

    public const int SE_FILE_OBJECT = 1;
    public const uint OWNER_SECURITY_INFORMATION = 0x1;
    public const uint DACL_SECURITY_INFORMATION = 0x4;
    public const uint PROTECTED_DACL_SECURITY_INFORMATION = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint length, IntPtr previous, IntPtr returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern uint SetNamedSecurityInfoW(string objectName, int objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
}

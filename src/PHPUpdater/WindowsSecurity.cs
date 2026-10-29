using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PHPUpdater;

public static class WindowsSecurity
{
    public static bool Elevated => new WindowsPrincipal(
        WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static void RequireUnelevated()
    {
        if (Elevated)
        {
            throw new InvalidOperationException(
                "User PHP and Composer probes must run without elevation.");
        }
    }

    public static void ProtectDirectory(string path)
    {
        SafeFiles.RejectLinks(path);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid, null);
        if (Directory.Exists(path))
        {
            var owner = new DirectoryInfo(path).GetAccessControl()
                .GetOwner(typeof(SecurityIdentifier));
            if (!administrators.Equals(owner) &&
                !new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Equals(owner))
            {
                throw new IOException("Existing managed directory has an " +
                    "untrusted owner: " + path);
            }
        }
        Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetOwner(administrators);
        security.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit |
            InheritanceFlags.ObjectInherit;
        foreach (var id in new[] { WellKnownSidType.BuiltinAdministratorsSid,
            WellKnownSidType.LocalSystemSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(id, null), FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(
            WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public static int RunUnelevated(string executable, IEnumerable<string> arguments)
    {
        executable = Path.GetFullPath(executable);
        var directory = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        if (!Elevated)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = directory
            };
            foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
            using var child = Process.Start(start)!;
            if (!child.WaitForExit(180000)) { child.Kill(true); return 1; }
            return child.ExitCode;
        }
        return RunWithShellParent(executable, arguments);
    }

    internal static int RunWithShellParent(string executable,
        IEnumerable<string> arguments)
    {
        executable = Path.GetFullPath(executable);
        var directory = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        using var shell = OpenShellProcess();
        nuint size = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == 0) { throw NativeFailure("Size process attributes"); }
        var attributes = Marshal.AllocHGlobal(checked((nint)size));
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                throw NativeFailure("Initialize process attributes");
            }
            try
            {
                return StartWithShellParent(shell, attributes,
                    executable, directory, arguments);
            }
            finally { DeleteProcThreadAttributeList(attributes); }
        }
        finally { Marshal.FreeHGlobal(attributes); }
    }

    private static SafeProcessHandle OpenShellProcess()
    {
        var window = GetShellWindow();
        if (window == IntPtr.Zero)
        {
            throw new InvalidOperationException("No interactive Windows shell " +
                "is available. Start setup in your signed-in desktop session.");
        }
        GetWindowThreadProcessId(window, out var processId);
        const uint createProcessAndQuery = 0x0080 | 0x1000;
        var shell = OpenProcess(createProcessAndQuery, false, processId);
        if (shell.IsInvalid)
        {
            var error = NativeFailure("Open Windows shell process");
            shell.Dispose();
            throw error;
        }
        try
        {
            ValidateShellIdentity(shell);
            return shell;
        }
        catch { shell.Dispose(); throw; }
    }

    private static void ValidateShellIdentity(SafeProcessHandle shell)
    {
        const uint tokenQuery = 0x0008;
        if (!OpenProcessToken(shell, tokenQuery, out var token))
        {
            throw NativeFailure("Read Windows shell identity");
        }
        using (token)
        {
            using var current = WindowsIdentity.GetCurrent();
            using var shellIdentity = new WindowsIdentity(token.DangerousGetHandle());
            if (current.User != shellIdentity.User)
            {
                throw new InvalidOperationException("Start setup using the same " +
                    "administrator account as the signed-in desktop user.");
            }
            const int tokenElevation = 20;
            if (!GetTokenInformation(token, tokenElevation, out var elevated,
                sizeof(int), out _))
            {
                throw NativeFailure("Check Windows shell elevation");
            }
            if (elevated != 0)
            {
                throw new InvalidOperationException("The Windows shell is elevated. " +
                    "Enable UAC and sign in normally before running setup.");
            }
        }
    }

    private static int StartWithShellParent(SafeProcessHandle shell,
        IntPtr attributes, string executable, string directory,
        IEnumerable<string> arguments)
    {
        var parent = shell.DangerousGetHandle();
        const nuint parentProcessAttribute = 0x00020000;
        if (!UpdateProcThreadAttribute(attributes, 0, parentProcessAttribute,
            ref parent, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            throw NativeFailure("Set non-elevated process parent");
        }
        var command = new StringBuilder(Quote(executable) + " " +
            string.Join(' ', arguments.Select(Quote)));
        var startup = new StartupInfoEx
        {
            Startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>() },
            Attributes = attributes
        };
        // The child inherits the verified shell token, not the setup token.
        const uint noWindowAndExtendedStartup = 0x08000000 | 0x00080000;
        if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero,
            false, noWindowAndExtendedStartup, IntPtr.Zero, directory,
            ref startup, out var process))
        {
            throw NativeFailure("Start non-elevated helper: " + executable);
        }
        CloseHandle(process.hThread);
        try
        {
            if (WaitForSingleObject(process.hProcess, 180000) != 0)
            {
                TerminateProcess(process.hProcess, 1);
                throw new TimeoutException("Unelevated validation timed out");
            }
            if (!GetExitCodeProcess(process.hProcess, out var code))
            {
                throw NativeFailure("Read helper exit code");
            }
            return (int)code;
        }
        finally { CloseHandle(process.hProcess); }
    }

    private static Win32Exception NativeFailure(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        return new Win32Exception(code,
            $"{operation} (Windows {code}): " + new Win32Exception(code).Message);
    }

    public static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? reserved, desktop, title;
        public int x, y, xSize, ySize, xChars, yChars, fill, flags;
        public short show, reservedSize;
        public IntPtr reserved2, stdin, stdout, stderr;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Startup;
        public IntPtr Attributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr hProcess, hThread;
        public int processId, threadId;
    }
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token,
        int type, out int information, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process,
        uint access, out SafeAccessTokenHandle token);
    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window,
        out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access,
        bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list,
        int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list,
        uint flags, nuint attribute, ref IntPtr value, nuint size,
        IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string application, StringBuilder command,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creation,
        IntPtr environment, string? directory, ref StartupInfoEx startup,
        out ProcessInfo process);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, int timeout);
    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint code);
}

using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace PHPUpdater;

public interface ISystemEnvironment
{
    string Path { get; set; }
}

public sealed class SystemEnvironment : ISystemEnvironment
{
    private const string Key = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    public string Path
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(Key)!;
            return (string?)key.GetValue("Path", "",
                RegistryValueOptions.DoNotExpandEnvironmentNames) ?? "";
        }
        set
        {
            using var key = Registry.LocalMachine.OpenSubKey(Key, true)!;
            var kind = key.GetValueKind("Path");
            key.SetValue("Path", value, kind);
            key.Flush();
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero,
                "Environment", 0x0002, 5000, out _);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message,
        IntPtr parameter, string text, uint flags, uint timeout, out IntPtr result);
}

using Microsoft.Win32;

namespace RsiWatcherGUI.Core;

public interface IAutostartService
{
    bool IsEnabled();
    void Enable();
    void Disable();
}

public sealed class RegistryAutostartService : IAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _appRunName;

    public RegistryAutostartService(string appRunName) => _appRunName = appRunName;

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        var val = key?.GetValue(_appRunName) as string;
        return !string.IsNullOrEmpty(val);
    }

    public void Enable()
    {
        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true) ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key!.SetValue(_appRunName, $"\"{exe}\"");
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        key?.DeleteValue(_appRunName, false);
    }
}

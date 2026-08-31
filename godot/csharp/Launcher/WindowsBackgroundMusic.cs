// Matthew W, 2026-08-12

using System.Runtime.InteropServices;
using System.Text;

namespace Nikami.Aurora.GodotRuntime.Launcher;

internal sealed class WindowsBackgroundMusic : IDisposable
{
    private readonly string alias = $"dragonagegodotmusic{Environment.ProcessId}";
    private bool isOpen;

    public bool TryPlayLooping(string path, bool muted, out string error)
    {
        Close();

        if (!OperatingSystem.IsWindows())
        {
            error = "The original WMA launcher music is currently supported only on Windows.";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"Launcher music was not found: {path}";
            return false;
        }

        if (!TrySend($"open \"{path}\" type mpegvideo alias {alias}", out error))
        {
            return false;
        }

        isOpen = true;
        if (!SetMuted(muted, out error) || !TrySend($"play {alias} repeat", out error))
        {
            Close();
            return false;
        }

        return true;
    }

    public bool SetMuted(bool muted, out string error)
    {
        if (!isOpen)
        {
            error = "Background music is not loaded.";
            return false;
        }

        return TrySend($"setaudio {alias} {(muted ? "off" : "on")}", out error);
    }

    public void Dispose()
    {
        Close();
    }

    public void Stop() => Close();

    private void Close()
    {
        if (!isOpen)
        {
            return;
        }

        _ = TrySend($"stop {alias}", out _);
        _ = TrySend($"close {alias}", out _);
        isOpen = false;
    }

    private static bool TrySend(string command, out string error)
    {
        var output = new StringBuilder(256);
        var result = MciSendString(command, output, output.Capacity, IntPtr.Zero);
        if (result == 0)
        {
            error = string.Empty;
            return true;
        }

        var errorText = new StringBuilder(256);
        _ = MciGetErrorString(result, errorText, errorText.Capacity);
        error = errorText.Length == 0
            ? $"Windows multimedia error {result}."
            : errorText.ToString();
        return false;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciSendStringW")]
    private static extern uint MciSendString(
        string command,
        StringBuilder output,
        int outputLength,
        IntPtr callback);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciGetErrorStringW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MciGetErrorString(
        uint errorCode,
        StringBuilder errorText,
        int errorTextLength);
}

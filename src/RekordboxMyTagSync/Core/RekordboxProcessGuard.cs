using System.Diagnostics;

namespace RekordboxMyTagSync.Core;

public static class RekordboxProcessGuard
{
    private static readonly string[] RekordboxProcessNames = ["rekordbox"];

    public static bool IsRunning()
    {
        foreach (var processName in RekordboxProcessNames)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch { return true; }

            try
            {
                if (processes.Length != 0) return true;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        return false;
    }
}

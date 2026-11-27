using System.Runtime.InteropServices;
using System.Threading;

namespace JumpListLauncher.Helpers;

/// <summary>
/// Named events a relaunched exe uses to forward its request (show popup, jump-list commands)
/// to the resident background instance. Must stay free of WPF types: Program.Main uses it before
/// WPF is loaded.
/// </summary>
public static class InstanceSignals
{
    public const string Show = "Groupa_Show_Event";
    public const string ClearCache = "Groupa_ClearCache_Event";
    public const string Settings = "Groupa_Settings_Event";
    public const string Preferences = "Groupa_Preferences_Event";

    /// <summary>Order matters: App's listener maps WaitAny indices to actions in this order.</summary>
    public static readonly string[] All = { Show, ClearCache, Settings, Preferences };

    public static string? EventFor(string[] args)
    {
        if (args.Length == 0) return Show;
        return args[0] switch
        {
            "--clear-cache" => ClearCache,
            "--settings" => Settings,
            "--preferences" => Preferences,
            _ => null
        };
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private const int ASFW_ANY = -1;

    /// <summary>
    /// Signal the resident instance for this command line.
    /// Returns false if no resident instance is listening for it (caller then runs normally).
    /// </summary>
    public static bool TrySignal(string[] args)
    {
        var name = EventFor(args);
        if (name == null) return false;

        try
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var evt)) return false;
            using (evt)
            {
                // This process was started by the user's click, so it may hand its foreground right to
                // the resident instance; otherwise the popup/dialog can open behind other windows.
                AllowSetForegroundWindow(ASFW_ANY);
                evt.Set();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}

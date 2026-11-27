using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace JumpListLauncher;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 1. Ultra-fast single-instance check BEFORE loading WPF / XAML / Themes
        // If Groupa is already running in background mode, signal it in ~1ms and exit immediately!
        if (args.Length == 0)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting("Groupa_Show_Event", out var existingEvt))
                {
                    existingEvt.Set();
                    existingEvt.Dispose();
                    return; // Zero WPF overhead, instant signal and exit!
                }
            }
            catch { }
        }

        // 2. Normal startup: run full WPF App
        RunApp();
    }

    // Kept in a separate non-inlined method so that Main itself never references WPF types:
    // the signal-and-exit path above then doesn't load PresentationFramework & co. at all.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

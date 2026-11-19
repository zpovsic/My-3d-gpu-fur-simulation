using System;
using System.Threading;
using System.Windows.Forms;

namespace DesktopMochi;

internal static class Program
{
    private const string MutexName = "Local\\DesktopMochiPuff_SingleInstance_v1";
    private const string WakeEventName = "Local\\DesktopMochiPuff_WakeEvent_v1";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var wake = EventWaitHandle.OpenExisting(WakeEventName);
                wake.Set();
            }
            catch
            {
                // Existing instance may still be starting up
            }
            return;
        }

        using var wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var form = new MochiForm(wakeEvent);
        Application.Run(form);
    }
}

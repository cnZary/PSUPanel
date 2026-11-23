namespace PSUPanel.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? initialPort = null;
        if (args.Length == 2 && args[0].Equals("--port", StringComparison.OrdinalIgnoreCase))
        {
            initialPort = args[1];
        }
        else if (args.Length != 0)
        {
            NativeMethods.MessageBox(
                IntPtr.Zero,
                "用法：PSUPanel.Tray.exe [--port COMx]",
                "PSUPanel",
                NativeMethods.MbOk | NativeMethods.MbIconWarning);
            return 2;
        }

        using var singleInstance = new Mutex(true, @"Local\PSUPanel.Tray.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            return 0;
        }

        try
        {
            using var monitor = new MonitorService(initialPort);
            monitor.Start();
            using var server = new SnapshotServer(monitor);
            using var tray = new TrayApplication(monitor);
            tray.Run();
            return 0;
        }
        catch (Exception exception)
        {
            NativeMethods.MessageBox(
                IntPtr.Zero,
                $"PSUPanel 启动失败：{exception.Message}",
                "PSUPanel",
                NativeMethods.MbOk | NativeMethods.MbIconError);
            return 1;
        }
    }
}

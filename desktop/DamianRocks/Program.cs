using System;
using System.Threading;
using System.Windows;

namespace DamianRocks;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = AppOptions.Parse(args, out string? error, out bool showHelp);
        if (options is null)
        {
            MessageBox.Show(showHelp ? AppOptions.Usage : $"{error}\n\n{AppOptions.Usage}", "Damian.Rocks",
                MessageBoxButton.OK, showHelp ? MessageBoxImage.Information : MessageBoxImage.Error);
            return showHelp ? 0 : 2;
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Local\DamianRocks.Overlay", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Damian.Rocks is already running. Look for its icon in the system tray.", "Damian.Rocks",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        var app = new OverlayApp(options);
        return app.Run();
    }
}

using LinuxRemoteWorker.Update;
using Velopack;

namespace LinuxRemoteWorker;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack's install, update and uninstall hooks run here and end the process when this start is one of
        // them. An update downloaded earlier is applied now - unless updates are switched off.
        VelopackApp.Build()
            .SetArgs(args)
            .SetAutoApplyOnStartup(!UpdateService.IsDisabled(args))
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

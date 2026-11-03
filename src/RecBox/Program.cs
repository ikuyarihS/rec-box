using Velopack;

namespace RecBox;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // must run first: handles install / update / uninstall hooks and exits early for them
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

using System.Runtime;

namespace LtcMtc;

static class Program
{
    [STAThread]
    static void Main()
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;   // keep GC pauses away from the audio threads
        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // dark mode is still flagged experimental
        Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001
        Application.Run(new MainForm());
    }
}

using VirtualLan.Core.Diagnostics;

namespace VirtualLan.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Processo GUI não tem console anexado; o log vai só para o painel da janela.
        Log.WriteToConsole = false;

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

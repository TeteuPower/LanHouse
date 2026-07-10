using VirtualLan.Core.Diagnostics;

namespace VirtualLan.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Processo GUI não tem console anexado; o log vai só para o painel da janela.
        Log.WriteToConsole = false;

        // Rede sadia: uma exceção inesperada num handler nunca deve encerrar o app em silêncio.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) ReportFatal(ex);
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void ReportFatal(Exception ex)
    {
        try
        {
            Log.Error("Erro inesperado", ex);
            MessageBox.Show(ex.Message, "VirtualLan — erro inesperado", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Se nem o diálogo de erro puder aparecer, não há mais o que fazer.
        }
    }
}

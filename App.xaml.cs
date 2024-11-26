using System;
using System.Windows;

namespace ImageViewer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                // Prøv å sette RenderMode (hvis relevant), eller fjern denne linjen
                Console.WriteLine("RenderMode er ikke eksplisitt satt. Standard brukes.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Feil under oppstart: {ex.Message}");
            }
        }
    }
}

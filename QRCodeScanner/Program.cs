namespace QRCodeScanner;

internal static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        
        // Set the application to run as a single instance
        bool createdNew;
        using (var mutex = new System.Threading.Mutex(true, "QRCodeScannerSingleInstance", out createdNew))
        {
            if (createdNew)
            {
                Application.Run(new Form1());
            }
            else
            {
                // Application already running - send message to existing instance
                // to bring it to front if needed (unless we're starting minimized)
                bool startMinimized = args.Length > 0 && args[0].Equals("/minimized", StringComparison.OrdinalIgnoreCase);
                if (!startMinimized)
                {
                    // In a real implementation, you would use Windows messaging to communicate 
                    // with the existing instance, but that's beyond the scope of this quick edit
                    MessageBox.Show("QR Code Scanner is already running.", "QR Code Scanner", 
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }
    }    
}
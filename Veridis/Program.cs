namespace Veridis
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Normal WinForms mode
            ApplicationConfiguration.Initialize();
            // Headless mode: run if single argument is a PDF file
            if (args.Length == 1 &&
                File.Exists(args[0]) &&
                string.Equals(Path.GetExtension(args[0]), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string pdfPath = args[0];
                    Form1.ExportFixedTxt(pdfPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error while processing invoice:\n{ex.Message}",
                        "Fix Invoice", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                return; // do not launch the WinForms UI
            }

            Application.Run(new Form1());
        }
    }
}

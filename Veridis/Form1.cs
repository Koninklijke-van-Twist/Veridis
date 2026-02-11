using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Veridis;

public partial class Form1 : Form
{
    public Form1()
    {
        InitializeComponent();
        contextMenuToggle.Checked = ContextMenuInstaller.IsInstalled();
    }

    private void contextMenuToggle_CheckedChanged(object sender, EventArgs e)
    {
        if(contextMenuToggle.Checked) 
        {
            try
            {
                string exePath = Application.ExecutablePath; // your running exe
                ContextMenuInstaller.Install(exePath);
                MessageBox.Show("‘Fix Invoice’ context menu installed for PDFs.", "Success");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Install failed:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        else
        {
            try
            {
                ContextMenuInstaller.Uninstall();
                MessageBox.Show("‘Fix Invoice’ context menu removed.", "Removed");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Uninstall failed:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void startButton_Click_1(object sender, EventArgs e)
    {
        if (openFileDialog1.ShowDialog() is not DialogResult.OK) return;

        ExportFixedTxt(openFileDialog1.FileName);

        MessageBox.Show($"Invoice fixed.\nPlease inspect the output to ensure the changes are correct.\n{openFileDialog1.FileName}",
                       "Fix Invoice", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public static void ExportFixedTxt(string fileName)
    {
        string pdfPath = fileName;
        string txtPath = Path.ChangeExtension(pdfPath, ".TXT");
        string outPath = Path.ChangeExtension(pdfPath, ".fixed.txt");

        InvoiceTxtFixer.FixTxtUsingPdf(txtPath, pdfPath, outPath);
    }
}
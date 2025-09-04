namespace Veridis;

public partial class Form1 : Form
{
    public Form1()
    {
        InitializeComponent();
    }

    private void startButton_Click_1(object sender, EventArgs e)
    {
        if (openFileDialog1.ShowDialog() is not DialogResult.OK) return;
        
        List<CaseLine> cases = PdfParser.ParseCaseDetails(openFileDialog1.FileName);
            
        foreach (CaseLine line in cases)
            Console.WriteLine(line);
    }
}
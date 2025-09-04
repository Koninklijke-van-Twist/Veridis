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
                var exePath = Application.ExecutablePath; // your running exe
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
    }

    public static void ExportFixedTxt(string fileName)
    {
        string pdfPath = fileName;
        string txtPath = Path.ChangeExtension(pdfPath, ".TXT");

        // 1) Parse PDF (for per-case allocations)
        var (headerFromPdf, detailsFromPdf, cases) = PdfParser.Parse(pdfPath);

        // 2) Parse supplier TXT (for header values + per-item attributes incl. CPC)
        SupplierTxtHeader txtHeader;
        List<SupplierTxtDetail> txtDetails;
        if (File.Exists(txtPath))
        {
            (txtHeader, txtDetails) = SupplierTxtParser.Parse(txtPath);

            // overwrite header fields from TXT (authoritative)
            headerFromPdf = headerFromPdf with
            {
                CustomerNumber = txtHeader.CustomerNumber,
                DeliveryAddressNumber = txtHeader.DeliveryAddressNumber,
                InvoiceNumber = txtHeader.InvoiceNumber,
                CurrencyCode = txtHeader.CurrencyCode,
                VatNumber = txtHeader.VatNumber
            };
        }
        else
        {
            MessageBox.Show("Text file not found. Make sure the original txt is in the same folder as the pdf and has the same name");
            return;
        }

        // 3) Build rows: prefer TXT detail fields, split by case from PDF
        var rows = CaseJoin.BuildCaseRecords(headerFromPdf, detailsFromPdf, cases, txtDetails).ToList();

        // 4) Totals: use TXT RT1 when available; otherwise compute from rows
        InvoiceTotals totals;
        if (File.Exists(txtPath))
        {
            totals = new InvoiceTotals
            {
                NetTotal = txtHeader.NetTotal,          // RT1 Nett Value
                GrandTotal = txtHeader.GrandTotal,      // RT1 Grand Total
                OtherCharges = Math.Max(0, Math.Round(txtHeader.GrandTotal - txtHeader.NetTotal, 2, MidpointRounding.AwayFromZero))
            };
        }
        else
        {
            totals = ComputeTotalsFromRowsAndPdf(rows, pdfPath); // your existing helper
        }

        // 5) Export
        string outPath = Path.ChangeExtension(pdfPath, ".fixed.txt");
        TextFileExporter.Export(outPath, headerFromPdf, rows, totals, includeLegend: true);
    }


    // ---- helpers ----

    private static InvoiceTotals ComputeTotalsFromRowsAndPdf(IReadOnlyList<CaseRecord2> rows, string pdfPath)
    {
        // Sum *per-line extended* values (to mimic supplier rounding behaviour)
        decimal netTotal = rows
            .Select(r => Math.Round(r.UnitNettValue * r.PickQuantity, 2, MidpointRounding.AwayFromZero))
            .Sum();

        // Try to read "Grand Total" / "Invoice Total" off the PDF text
        decimal? grand = TryReadGrandTotalFromPdf(pdfPath);

        // If we didn’t find it, assume no extras
        decimal grandTotal = grand ?? netTotal;

        // Carriage/others = Grand − Net (don’t allow tiny negative due to FP)
        decimal other = Math.Round(grandTotal - netTotal, 2, MidpointRounding.AwayFromZero);
        if (other < 0) other = 0;

        return new InvoiceTotals
        {
            NetTotal = netTotal,
            GrandTotal = grandTotal,
            OtherCharges = other
        };
    }

    private static decimal? TryReadGrandTotalFromPdf(string pdfPath)
    {
        // Flexible number matcher: 12,345.67 or 12345.67
        var num = @"(?<v>\d{1,3}(?:,\d{3})*(?:\.\d{2})|\d+\.\d{2})";
        var rxGrand = new Regex($@"Grand\s*Total\s*{num}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var rxInvoice = new Regex($@"Invoice\s*Total\s*{num}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var rxAmount = new Regex($@"Total\s*Amount\s*{num}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        using var doc = PdfDocument.Open(pdfPath);
        foreach (var page in doc.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page);
            foreach (var rx in new[] { rxGrand, rxInvoice, rxAmount })
            {
                var m = rx.Match(text);
                if (m.Success && decimal.TryParse(
                        m.Groups["v"].Value.Replace(",", ""),
                        NumberStyles.Number,
                        CultureInfo.InvariantCulture,
                        out var val))
                {
                    return Math.Round(val, 2, MidpointRounding.AwayFromZero);
                }
            }
        }
        return null;
    }
}
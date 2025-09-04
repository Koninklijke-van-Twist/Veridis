using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Veridis;

public record CaseLine(string ProductId, string HandlingUnit, int Quantity, string DeliveryNumber, string ProductDescription, string CountryOfOrigin)
{
    public static implicit operator string(CaseLine line) => $"{line.CountryOfOrigin} Box {line.HandlingUnit}: Item {line.ProductDescription}({line.ProductId}) x {line.Quantity}. Part of delivery {line.DeliveryNumber}.";
}

public static class PdfParser
{
    // Robust to variable spacing; anchors to country code (2 letters) before qty.
    private static readonly Regex Row = new(
    @"^(?<hu>\d{10})\s+(?<delivery>\d+)\s+(?<item>[A-Z0-9]+)\s+(?<desc>.+?)\s+(?<country>[A-Z]{2})\s+(?<qty>\d+)\s*$",
    RegexOptions.Compiled);

    public static List<CaseLine> ParseCaseDetails(string pdfPath)
    {
        List<CaseLine> lines = new List<CaseLine>();
        bool inCaseDetails = false;

        using PdfDocument doc = PdfDocument.Open(pdfPath);
        foreach (Page page in doc.GetPages())
        {
            // Get a reasonably ordered plain-text dump of the page
            string? text = ContentOrderTextExtractor.GetText(page);

            // Flip into/out of section mode
            if (text.Contains("Case Details", StringComparison.OrdinalIgnoreCase))
                inCaseDetails = true;
            if (text.Contains("General Summary", StringComparison.OrdinalIgnoreCase))
                inCaseDetails = false;

            if (!inCaseDetails) continue;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                // Skip headers
                if (line.StartsWith("Handling Unit", StringComparison.OrdinalIgnoreCase)) continue;

                Match m = Row.Match(line);
                if (m.Success)
                {
                    string hu = m.Groups["hu"].Value;
                    string delivery = m.Groups["delivery"].Value;
                    string item = m.Groups["item"].Value;
                    string desc = m.Groups["desc"].Value.Trim();
                    string country = m.Groups["country"].Value;
                    int qty = int.Parse(m.Groups["qty"].Value);

                    lines.Add(new CaseLine(item, hu, qty, delivery, desc, country));
                }
            }
        }

        return lines;
    }
}

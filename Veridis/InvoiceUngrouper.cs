using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

public static class InvoiceTxtFixer
{
    public record CaseDetail(string Hu, string DeliveryNumber, string ProductId, string Country, int Quantity);

    public static void FixTxtUsingPdf(string txtPath, string pdfPath, string outputPath)
    {
        // 1) Parse PDF Case Details -> list
        var caseDetails = ParseCaseDetailsFromPdf(pdfPath);

        // Index op ProductId voor snelle lookup (bewaar volgorde zoals in PDF)
        var byProduct = caseDetails
            .GroupBy(x => x.ProductId)
            .ToDictionary(g => g.Key, g => g.ToList());

        Console.WriteLine($"CaseDetails parsed: {caseDetails.Count}");
        Console.WriteLine($"Distinct Product IDs: {byProduct.Count}");

        // 2) Lees TXT en schrijf output
        using var sr = new StreamReader(txtPath, Encoding.UTF8, true);
        using var sw = new StreamWriter(outputPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                sw.WriteLine(line);
                continue;
            }

            // Laat headers "Record Type ..." altijd 1:1 door
            if (line.StartsWith("Record Type", StringComparison.OrdinalIgnoreCase))
            {
                sw.WriteLine(line);
                continue;
            }

            // CSV parse (werkt met quotes)
            var cols = CsvSplit(line);
            if (cols.Count == 0)
            {
                sw.WriteLine(line);
                continue;
            }

            // We fixen alleen record type 2 regels (eerste kolom "2")
            if (Unquote(cols[0]) != "2")
            {
                sw.WriteLine(line);
                continue;
            }

            // Kolommen volgens jouw header:
            // 0=type, 1=CustomerNumber, 2=DeliveryAddressNumber, 3=InvoiceNumber,
            // 4=CustomerOrderNumber, 5=SuppliedPartNumber, 6=PickQuantity, 7=NettValue,
            // ...
            // 15=HU
            string productId = Unquote(Get(cols, 5));
            string huRaw = Unquote(Get(cols, 15));

            // Geen / in HU? -> 1:1 door
            if (string.IsNullOrWhiteSpace(huRaw) || !huRaw.Contains('/'))
            {
                sw.WriteLine(line);
                continue;
            }

            // Deze regel moet gesplitst worden met PDF als bron van waarheid
            decimal origQty = ParseDec(Unquote(Get(cols, 6)));
            decimal origNett = ParseDec(Unquote(Get(cols, 7)));
            decimal unitPrice = (origQty == 0) ? 0 : (origNett / origQty);

            var huSet = huRaw
    .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(PadHu20) // dezelfde PadHu20 als hierboven
    .ToHashSet(StringComparer.Ordinal);



            if (!byProduct.TryGetValue(productId, out var pdfRowsAll))
            {
                // Geen match in PDF? Dan liever NIET gokken: schrijf origineel door (en je kunt elders loggen)
                sw.WriteLine(line);
                Console.Error.WriteLine($"Geen match gevonden in PDF voor productId={productId} | HU={huRaw} | line={line}");
                continue;
            }

            // Filter op HU’s die de TXT noemde (safety), maar als dat 0 oplevert kun je ook fallbacken naar alles.
            var pdfRows = pdfRowsAll.Where(r => huSet.Contains(r.Hu)).ToList();
            if (pdfRows.Count == 0)
            {
                // Fallback: alles voor productId (kan nuttig zijn als TXT HU’s net anders zijn)
                pdfRows = pdfRowsAll.ToList();
            }

            // Maak outputregels: één per PDF case-detail rij, exact zoals PDF (niet aggregeren)
            foreach (var r in pdfRows)
            {
                var newCols = new List<string>(cols);

                // HU kolom (15) padded naar 20 digits zoals jij wil
                newCols[15] = Quote(PadHu20(r.Hu));

                // PickQuantity (6) = PDF quantity
                newCols[6] = Quote(r.Quantity.ToString("0.000", CultureInfo.InvariantCulture));

                // NettValue (7) = unitPrice * qty (2 decimals is typisch voor currency; pas aan als jouw TXT anders is)
                decimal newNett = Round2(unitPrice * r.Quantity);
                newCols[7] = Quote(newNett.ToString("0.00", CultureInfo.InvariantCulture));

                sw.WriteLine(string.Join(",", newCols));
            }
        }
    }

    static readonly Regex RxCaseRow = new Regex(
    @"(?<hu>\d{10})\s+(?<del>\d{9})\s+(?<pid>[A-Z0-9][A-Z0-9\-]{3,})\s+" +
    @"(?<desc>.*?)\s+(?<coo>[A-Z]{2})\s+(?<qty>\d+)" +
    @"(?=\s+\d{10}\s+\d{9}\s+[A-Z0-9][A-Z0-9\-]{3,}\s+|$)",
    RegexOptions.Compiled | RegexOptions.Singleline
);


    private static List<CaseDetail> ParseCaseDetailsFromPdf(string pdfPath)
    {
        var results = new List<CaseDetail>();

        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdfPath);
        foreach (var page in doc.GetPages())
        {
            var text = page.Text ?? "";
            if (!text.Contains("Case Details", StringComparison.OrdinalIgnoreCase))
                continue;

            // Pak substring vanaf "Handling Unit" (header begint daar)
            var start = text.IndexOf("Handling Unit", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                continue;

            var chunk = text.Substring(start);

            // Optioneel: knip af bij duidelijke footer/volgende sectie als die voorkomt
            var end = chunk.IndexOf("Invoice Detail", StringComparison.OrdinalIgnoreCase);
            if (end > 0) chunk = chunk.Substring(0, end);

            var matches = RxCaseRow.Matches(chunk);
            foreach (Match m in matches)
            {
                var hu20 = m.Groups["hu"].Value.PadLeft(20, '0');
                var del = m.Groups["del"].Value;
                var pid = m.Groups["pid"].Value;
                var coo = m.Groups["coo"].Value;

                if (!int.TryParse(m.Groups["qty"].Value, out var qty))
                    continue;

                results.Add(new CaseDetail(
                    Hu: hu20,
                    DeliveryNumber: del,
                    ProductId: pid,
                    Country: coo,
                    Quantity: qty
                ));
            }
        }

        return results;
    }


    private static int FindToken(List<string> tokens, string token)
    {
        for (int i = 0; i < tokens.Count; i++)
            if (tokens[i].Equals(token, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static bool IsDigits(string s) => !string.IsNullOrEmpty(s) && s.All(char.IsDigit);
    private static bool IsHu10(string s) => s?.Length == 10 && IsDigits(s);
    private static bool IsCountry2(string s) => s?.Length == 2 && s.All(char.IsLetter);

    private static string PadHu20(string hu10)
    {
        var digits = new string((hu10 ?? "").Where(char.IsDigit).ToArray());
        return digits.PadLeft(20, '0');
    }

    // --- helpers ---

    private static string Get(List<string> cols, int idx) => (idx >= 0 && idx < cols.Count) ? cols[idx] : "";

    private static decimal ParseDec(string s)
    {
        // support "198,250" and "198.250"
        s = (s ?? "").Trim();
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var a)) return a;
        if (decimal.TryParse(s, NumberStyles.Any, new CultureInfo("nl-NL"), out var b)) return b;
        return 0m;
    }

    private static decimal Round2(decimal v) =>
        Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static string Quote(string s) => $"\"{(s ?? "").Replace("\"", "\"\"")}\"";
    private static string Unquote(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\""))
            return s.Substring(1, s.Length - 2).Replace("\"\"", "\"");
        return s;
    }

    private static List<string> CsvSplit(string line)
    {
        var result = new List<string>();
        if (line == null) return result;

        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        result.Add(sb.ToString());
        return result;
    }
}

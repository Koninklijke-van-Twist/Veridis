using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

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
                newCols[15] = PadHu20(r.Hu);

                // PickQuantity (6) = PDF quantity
                newCols[6] = r.Quantity.ToString("0.000", CultureInfo.InvariantCulture);

                // NettValue (7) = unitPrice * qty (2 decimals is typisch voor currency; pas aan als jouw TXT anders is)
                decimal newNett = Round2(unitPrice * r.Quantity);
                newCols[7] = newNett.ToString("0.00", CultureInfo.InvariantCulture);

                for (int i = 0; i < newCols.Count; i++)
                    newCols[i] = Quote(newCols[i]);

                sw.WriteLine(string.Join(",", newCols));
            }
        }
    }

    static readonly Regex RxCaseRow = new Regex(
    @"(?<hu>\d{10})\s+(?<del>\d+)\s+(?<pid>\S+)\s+.*?\s+(?<coo>[A-Z]{2})\s*(?<qty>\d+)\b" +
    @"(?=\s+\d{10}\s+\d+\s+\S+|\s*$)",
    RegexOptions.Compiled | RegexOptions.Singleline);

    public static List<CaseDetail> ParseCaseDetailsFromPdf(string pdfPath)
    {
        var results = new List<CaseDetail>();

        using var doc = PdfDocument.Open(pdfPath);

        foreach (var page in doc.GetPages())
        {
            // snelle filter: alleen pagina’s waar “Case Details” op staat
            var pageText = page.Text ?? "";
            if (!pageText.Contains("Case Details", StringComparison.OrdinalIgnoreCase))
                continue;

            // 1) Words ophalen en groeperen tot “regels” op Y
            var words = page.GetWords().ToList();

            // Sorteer: boven naar beneden (Y hoog -> laag), links naar rechts (X laag -> hoog)
            words.Sort((a, b) =>
            {
                var dy = b.BoundingBox.Bottom.CompareTo(a.BoundingBox.Bottom);
                return dy != 0 ? dy : a.BoundingBox.Left.CompareTo(b.BoundingBox.Left);
            });

            // Groepeer op “lijn” met Y-tolerantie
            var lines = new List<List<Word>>();
            const double yTol = 1.5; // soms 1.0 of 2.0 beter; 1.5 is vaak oké

            foreach (var w in words)
            {
                if (string.IsNullOrWhiteSpace(w.Text))
                    continue;

                var y = w.BoundingBox.Bottom;

                var last = lines.LastOrDefault();
                if (last == null)
                {
                    lines.Add(new List<Word> { w });
                    continue;
                }

                var lastY = last[0].BoundingBox.Bottom;
                if (Math.Abs(lastY - y) <= yTol)
                    last.Add(w);
                else
                    lines.Add(new List<Word> { w });
            }

            // 2) Maak tekstregels en parse “Case Details” rijen
            foreach (var lineWords in lines)
            {
                var ordered = lineWords.OrderBy(w => w.BoundingBox.Left).ToList();
                var line = string.Join(" ", ordered.Select(w => w.Text)).Trim();

                // We verwachten: HU(10 digits) Delivery(digits) ProductId ... Country(2 letters) Qty(int)
                // Voorbeeld: 4401762522 100794958 5589401 OIL FILTER TN 189
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length < 6)
                    continue;

                if (!(tokens[0].Length == 10 && tokens[0].All(char.IsDigit)))
                    continue;

                var hu20 = tokens[0].PadLeft(20, '0');
                var delivery = tokens[1];
                var productId = tokens[2];

                // Laatste token qty, voorlaatste country
                var qtyTok = tokens[^1];
                var cooTok = tokens[^2];

                if (!int.TryParse(qtyTok, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty))
                    continue;

                if (!(cooTok.Length == 2 && cooTok.All(char.IsLetter)))
                    continue;

                results.Add(new CaseDetail(
                    Hu: hu20,
                    DeliveryNumber: delivery,
                    ProductId: productId,
                    Country: cooTok.ToUpperInvariant(),
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

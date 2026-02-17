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
        List<CaseDetail> caseDetails = ParseCaseDetailsFromPdf(pdfPath);

        // Build quick lookup: HU -> (ProductId -> remaining qty)
        var pdfInventory = BuildPdfInventory(caseDetails);

        // Index op ProductId voor snelle lookup (bewaar volgorde zoals in PDF)
        Dictionary<string, List<CaseDetail>> byProduct = caseDetails
            .GroupBy(x => x.ProductId)
            .ToDictionary(g => g.Key, g => g.ToList());

        Console.WriteLine($"CaseDetails parsed: {caseDetails.Count}");
        Console.WriteLine($"Distinct Product IDs: {byProduct.Count}");
                            
        // Keep assignments for later verification (HU -> Product -> allocated qty)
        var assignments = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        // 2) Lees TXT en schrijf output
        // Use explicit using-blocks so the writer is disposed (closed) before verification runs.
        using (var sr = new StreamReader(txtPath, Encoding.UTF8, true))
        {
            using (var sw = new StreamWriter(outputPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
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
                    List<string> cols = CsvSplit(line);
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
                    string deliveryAddressNumber = Unquote(Get(cols, 2));
                    string productId = Unquote(Get(cols, 5));
                    string huRaw = Unquote(Get(cols, 15));

                    // Normaal: geen / in HU -> 1:1 door, maar we still record assignment
                    if (string.IsNullOrWhiteSpace(huRaw) || !huRaw.Contains('/'))
                    {
                        sw.WriteLine(line);

                        // record assignment from this line to pdfInventory if possible (reserve)
                        var singleHu = PadHu20(huRaw);
                        int allocated = ToIntQuantity(ParseDec(Unquote(Get(cols, 6))));
                        ReserveAssignment(assignments, pdfInventory, singleHu, productId, allocated);

                        continue;
                    }

                    // Deze regel moet gesplitst worden met PDF als bron van waarheid
                    decimal origQtyDec = ParseDec(Unquote(Get(cols, 6)));
                    int remaining = ToIntQuantity(origQtyDec);
                    decimal origNett = ParseDec(Unquote(Get(cols, 7)));
                    decimal unitPrice = (origQtyDec == 0m) ? 0m : (origNett / origQtyDec);

                    // HU list as in TXT (preserve order)
                    var huList = huRaw
                        .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(PadHu20)
                        .ToList();

                    var allocations = new List<(string Hu, int Qty)>();

                    // First try to allocate from the HUs explicitly listed, not exceeding PDF capacity per HU.
                    foreach (var hu in huList)
                    {
                        if (remaining <= 0) break;
                        int available = GetAvailable(pdfInventory, hu, productId);
                        if (available <= 0) continue;
                        int allocate = Math.Min(available, remaining);
                        if (allocate <= 0) continue;

                        allocations.Add((hu, allocate));
                        DecreaseAvailable(pdfInventory, hu, productId, allocate);
                        ReserveAssignment(assignments, pdfInventory: null, hu, productId, allocate); // record only
                        remaining -= allocate;
                    }

                    // If still remaining, fallback: allocate from any HU that contains this product in PDF (but still do not exceed HU capacity).
                    if (remaining > 0 && byProduct.TryGetValue(productId, out var allPdfRowsForProduct))
                    {
                        // iterate by order found in PDF
                        foreach (var pdfRow in allPdfRowsForProduct)
                        {
                            if (remaining <= 0) break;
                            string hu = pdfRow.Hu;
                            int available = GetAvailable(pdfInventory, hu, productId);
                            if (available <= 0) continue;
                            int allocate = Math.Min(available, remaining);
                            allocations.Add((hu, allocate));
                            DecreaseAvailable(pdfInventory, hu, productId, allocate);
                            ReserveAssignment(assignments, pdfInventory: null, hu, productId, allocate); // record only
                            remaining -= allocate;
                        }
                    }

                    if (allocations.Count == 0)
                    {
                        // Nothing could be allocated from PDF — safer to skip splitting and log
                        Console.Error.WriteLine($"Could not allocate product {productId} for TXT HU list '{huRaw}' (line kept as-is).");
                        sw.WriteLine(line);
                        continue;
                    }

                    if (remaining > 0)
                    {
                        // Not fully allocated, log. We still write allocated lines.
                        Console.Error.WriteLine($"Partial allocation for product {productId} HU list '{huRaw}': requested {origQtyDec}, allocated {origQtyDec - remaining}.");
                    }

                    // Make output lines: one per allocation (HU)
                    foreach (var alloc in allocations)
                    {
                        var newCols = new List<string>(cols);

                        // HU kolom (15) padded naar 20 digits zoals jij wil
                        newCols[15] = PadHu20(alloc.Hu);

                        // PickQuantity (6) = allocated quantity
                        newCols[6] = alloc.Qty.ToString("0.000", CultureInfo.InvariantCulture);

                        // NettValue (7) = unitPrice * qty (2 decimals)
                        decimal newNett = Round2(unitPrice * alloc.Qty);
                        newCols[7] = newNett.ToString("0.00", CultureInfo.InvariantCulture);

                        for (int i = 0; i < newCols.Count; i++)
                            newCols[i] = Quote(newCols[i]);

                        sw.WriteLine(string.Join(",", newCols));
                    }
                } // end while
            } // sw disposed here
        } // sr disposed here

        // 3) Verification: lees fixed file en controleer dat per HU+Product de quantities overeenkomen met PDF Case Details
        var verificationReportPath = Path.ChangeExtension(outputPath, ".verify.txt");
        VerifyFixedAgainstPdf(outputPath, pdfPath, verificationReportPath);
    }

    // Build HU -> ProductId -> remaining qty dictionary
    private static Dictionary<string, Dictionary<string, int>> BuildPdfInventory(List<CaseDetail> caseDetails)
    {
        var inv = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var cd in caseDetails)
        {
            if (!inv.TryGetValue(cd.Hu, out var prodMap))
            {
                prodMap = new Dictionary<string, int>(StringComparer.Ordinal);
                inv[cd.Hu] = prodMap;
            }

            prodMap.TryGetValue(cd.ProductId, out int current);
            prodMap[cd.ProductId] = current + cd.Quantity;
        }
        return inv;
    }

    private static int GetAvailable(Dictionary<string, Dictionary<string, int>> inv, string hu, string productId)
    {
        if (inv == null) return 0;
        if (!inv.TryGetValue(hu, out var prodMap)) return 0;
        if (!prodMap.TryGetValue(productId, out var qty)) return 0;
        return qty;
    }

    private static void DecreaseAvailable(Dictionary<string, Dictionary<string, int>> inv, string hu, string productId, int amount)
    {
        if (inv == null) return;
        if (!inv.TryGetValue(hu, out var prodMap)) return;
        if (!prodMap.TryGetValue(productId, out var qty)) return;
        qty -= amount;
        if (qty <= 0)
            prodMap.Remove(productId);
        else
            prodMap[productId] = qty;
        if (prodMap.Count == 0)
            inv.Remove(hu);
    }

    // Records assignment in assignments dictionary; if pdfInventory reference is provided, you may also decrement there.
    private static void ReserveAssignment(Dictionary<string, Dictionary<string, int>> assignments, Dictionary<string, Dictionary<string, int>>? pdfInventory, string hu, string productId, int qty)
    {
        if (!assignments.TryGetValue(hu, out var map))
        {
            map = new Dictionary<string, int>(StringComparer.Ordinal);
            assignments[hu] = map;
        }

        map.TryGetValue(productId, out int cur);
        map[productId] = cur + qty;

        // optional: also decrement pdfInventory if provided
        if (pdfInventory != null)
        {
            DecreaseAvailable(pdfInventory, hu, productId, qty);
        }
    }

    // Re-assign decimals -> integer units (use AwayFromZero rounding)
    private static int ToIntQuantity(decimal q) =>
        (int)Math.Round(q, 0, MidpointRounding.AwayFromZero);

    public static List<CaseDetail> ParseCaseDetailsFromPdf(string pdfPath)
    {
        List<CaseDetail> results = new List<CaseDetail>();

        using PdfDocument doc = PdfDocument.Open(pdfPath);

        foreach (Page page in doc.GetPages())
        {
            // snelle filter: alleen pagina’s waar “Case Details” op staat
            string pageText = page.Text ?? "";
            if (!pageText.Contains("Case Details", StringComparison.OrdinalIgnoreCase))
                continue;

            // 1) Words ophalen en groeperen tot “regels” op Y
            List<Word> words = page.GetWords().ToList();

            // Sorteer: boven naar beneden (Y hoog -> laag), links naar rechts (X laag -> hoog)
            words.Sort((a, b) =>
            {
                int dy = b.BoundingBox.Bottom.CompareTo(a.BoundingBox.Bottom);
                return dy != 0 ? dy : a.BoundingBox.Left.CompareTo(b.BoundingBox.Left);
            });

            // Groepeer op “lijn” met Y-tolerantie
            List<List<Word>> lines = new List<List<Word>>();
            const double yTol = 1.5; // soms 1.0 of 2.0 beter; 1.5 is vaak oké

            foreach (Word? w in words)
            {
                if (string.IsNullOrWhiteSpace(w.Text))
                    continue;

                double y = w.BoundingBox.Bottom;

                List<Word>? last = lines.LastOrDefault();
                if (last == null)
                {
                    lines.Add(new List<Word> { w });
                    continue;
                }

                double lastY = last[0].BoundingBox.Bottom;
                if (Math.Abs(lastY - y) <= yTol)
                    last.Add(w);
                else
                    lines.Add(new List<Word> { w });
            }

            // 2) Maak tekstregels en parse “Case Details” rijen
            foreach (List<Word> lineWords in lines)
            {
                List<Word> ordered = lineWords.OrderBy(w => w.BoundingBox.Left).ToList();
                string line = string.Join(" ", ordered.Select(w => w.Text)).Trim();

                // We verwachten: HU(10 digits) Delivery(digits) ProductId ... Country(2 letters) Qty(int)
                // Voorbeeld: 4401762522 100794958 5589401 OIL FILTER TN 189
                string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length < 6)
                    continue;

                if (!(tokens[0].Length == 10 && tokens[0].All(char.IsDigit)))
                    continue;

                string hu20 = tokens[0].PadLeft(20, '0');
                string delivery = tokens[1];
                string productId = tokens[2];

                // Laatste token qty, voorlaatste country
                string qtyTok = tokens[^1];
                string cooTok = tokens[^2];

                if (!int.TryParse(qtyTok, NumberStyles.Integer, CultureInfo.InvariantCulture, out int qty))
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

    private static void VerifyFixedAgainstPdf(string fixedFilePath, string pdfPath, string reportPath)
    {
        // Read PDF case details and compute expected HU+Product totals
        var pdfCaseDetails = ParseCaseDetailsFromPdf(pdfPath);
        var expected = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var cd in pdfCaseDetails)
        {
            if (!expected.TryGetValue(cd.Hu, out var m)) { m = new Dictionary<string, int>(StringComparer.Ordinal); expected[cd.Hu] = m; }
            m.TryGetValue(cd.ProductId, out int cur);
            m[cd.ProductId] = cur + cd.Quantity;
        }

        // Read fixed file and compute actual HU+Product totals
        var actual = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        using var sr = new StreamReader(fixedFilePath, Encoding.UTF8, true);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = CsvSplit(line);
            if (cols.Count == 0) continue;
            if (Unquote(cols[0]) != "2") continue;

            string productId = Unquote(Get(cols, 5));
            string hu = PadHu20(Unquote(Get(cols, 15)));
            int qty = ToIntQuantity(ParseDec(Unquote(Get(cols, 6))));

            if (!actual.TryGetValue(hu, out var m)) { m = new Dictionary<string, int>(StringComparer.Ordinal); actual[hu] = m; }
            m.TryGetValue(productId, out int cur);
            m[productId] = cur + qty;
        }

        // Compare expected vs actual (for every HU and product present in either)
        var sb = new StringBuilder();
        bool allGood = true;
        sb.AppendLine($"Verification report for '{Path.GetFileName(fixedFilePath)}' vs PDF '{Path.GetFileName(pdfPath)}'");
        sb.AppendLine();

        // collect union of keys
        var hus = new HashSet<string>(expected.Keys, StringComparer.Ordinal);
        foreach (var k in actual.Keys) hus.Add(k);

        foreach (var hu in hus.OrderBy(x => x))
        {
            sb.AppendLine($"HU: {hu}");
            var prodKeys = new HashSet<string>(StringComparer.Ordinal);
            if (expected.TryGetValue(hu, out var expMap)) foreach (var k in expMap.Keys) prodKeys.Add(k);
            if (actual.TryGetValue(hu, out var actMap)) foreach (var k in actMap.Keys) prodKeys.Add(k);

            foreach (var pid in prodKeys.OrderBy(x => x))
            {
                int exp = expMap != null && expMap.TryGetValue(pid, out var e) ? e : 0;
                int act = actMap != null && actMap.TryGetValue(pid, out var a) ? a : 0;
                if (exp != act) allGood = false;
                sb.AppendLine($"  Product {pid}: expected={exp}, actual={act}{(exp != act ? "  <-- MISMATCH" : "")}");
            }

            sb.AppendLine();
        }

        sb.AppendLine(allGood ? "VERIFICATION OK: all HU/product totals match PDF." : "VERIFICATION FAILED: mismatches found. See above.");
        File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"Verification written to {reportPath}");
        if (!allGood)
            Console.Error.WriteLine($"Verification failed, see {reportPath}");
    }

    private static string PadHu20(string hu10)
    {
        string digits = new string((hu10 ?? "").Where(char.IsDigit).ToArray());
        return digits.PadLeft(20, '0');
    }

    // --- helpers ---

    private static string Get(List<string> cols, int idx) => (idx >= 0 && idx < cols.Count) ? cols[idx] : "";

    private static decimal ParseDec(string s)
    {
        // support "198,250" and "198.250"
        s = (s ?? "").Trim();
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal a)) return a;
        if (decimal.TryParse(s, NumberStyles.Any, new CultureInfo("nl-NL"), out decimal b)) return b;
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
        List<string> result = new List<string>();
        if (line == null) return result;

        StringBuilder sb = new StringBuilder();
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

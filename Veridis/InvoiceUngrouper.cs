using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
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


    // Line info for record type 2 rows
    record LineInfo(int Index, List<string> Cols, string ProductId, string Hu, decimal Qty, decimal Nett, decimal UnitPrice);

    private static void VerifyFixedAgainstPdf(string fixedFilePath, string pdfPath, string reportPath)
    {
        // 1) compute expected from PDF
        var pdfCaseDetails = ParseCaseDetailsFromPdf(pdfPath);
        var expected = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var cd in pdfCaseDetails)
        {
            if (!expected.TryGetValue(cd.Hu, out var m)) { m = new Dictionary<string, int>(StringComparer.Ordinal); expected[cd.Hu] = m; }
            m.TryGetValue(cd.ProductId, out int cur);
            m[cd.ProductId] = cur + cd.Quantity;
        }

        // 2) read fixed file lines
        var origLines = File.ReadAllLines(fixedFilePath, Encoding.UTF8).ToList();

        var infos = new List<LineInfo>();

        for (int i = 0; i < origLines.Count; i++)
        {
            var raw = origLines[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cols = CsvSplit(raw);
            if (cols.Count == 0) continue;
            if (Unquote(cols[0]) != "2") continue;

            string productId = Unquote(Get(cols, 5));
            string hu = PadHu20(Unquote(Get(cols, 15)));
            decimal qty = ParseDec(Unquote(Get(cols, 6)));
            decimal nett = ParseDec(Unquote(Get(cols, 7)));
            decimal unitPrice = qty != 0 ? nett / qty : 0m;

            infos.Add(new LineInfo(i, cols, productId, hu, qty, nett, unitPrice));
        }

        // 3) compute actual totals
        var actual = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var info in infos)
        {
            int q = ToIntQuantity(info.Qty);
            if (!actual.TryGetValue(info.Hu, out var m)) { m = new Dictionary<string, int>(StringComparer.Ordinal); actual[info.Hu] = m; }
            m.TryGetValue(info.ProductId, out int cur);
            m[info.ProductId] = cur + q;
        }

        // 4) collect mismatches (diff = actual - expected)
        var mismatches = new Dictionary<(string Hu, string Product), int>();
        var allHus = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in expected.Keys) allHus.Add(k);
        foreach (var k in actual.Keys) allHus.Add(k);

        foreach (var hu in allHus)
        {
            var prodKeys = new HashSet<string>(StringComparer.Ordinal);
            if (expected.TryGetValue(hu, out var expMap)) foreach (var k in expMap.Keys) prodKeys.Add(k);
            if (actual.TryGetValue(hu, out var actMap)) foreach (var k in actMap.Keys) prodKeys.Add(k);

            foreach (var pid in prodKeys)
            {
                int exp = expMap != null && expMap.TryGetValue(pid, out var e) ? e : 0;
                int act = actMap != null && actMap.TryGetValue(pid, out var a) ? a : 0;
                int diff = act - exp;
                if (diff != 0)
                    mismatches[(hu, pid)] = diff;
            }
        }

        // 5) attempt automated fixes by rebalancing surpluses -> deficits per product
        var fixes = new List<string>();
        // prepare per-product surplus/deficit lists
        var byProductSurplus = new Dictionary<string, List<(string Hu, int Qty)>>(StringComparer.Ordinal);
        var byProductDeficit = new Dictionary<string, List<(string Hu, int Qty)>>(StringComparer.Ordinal);

        foreach (var kv in mismatches)
        {
            var (hu, pid) = kv.Key;
            int diff = kv.Value;
            if (diff > 0)
            {
                if (!byProductSurplus.TryGetValue(pid, out var list)) { list = new List<(string, int)>(); byProductSurplus[pid] = list; }
                list.Add((hu, diff));
            }
            else
            {
                if (!byProductDeficit.TryGetValue(pid, out var list)) { list = new List<(string, int)>(); byProductDeficit[pid] = list; }
                list.Add((hu, -diff)); // store positive needed amount
            }
        }

        // Helper actions: decrease from source HU by amount (may span multiple lines)
        void DecreaseFromSource(string productId, string sourceHu, int amount)
        {
            int remaining = amount;
            var candidates = infos.Where(x => x.ProductId == productId && x.Hu == sourceHu && ToIntQuantity(x.Qty) > 0).OrderBy(x => x.Index).ToList();
            foreach (var info in candidates)
            {
                if (remaining <= 0) break;
                int lineQty = ToIntQuantity(info.Qty);
                int take = Math.Min(lineQty, remaining);

                // update the cols for this line in origLines
                var cols = info.Cols;
                decimal newQty = lineQty - take;
                decimal unitPrice = info.UnitPrice;
                decimal newNett = Round2(unitPrice * newQty);

                cols[6] = newQty.ToString("0.000", CultureInfo.InvariantCulture);
                cols[7] = newNett.ToString("0.00", CultureInfo.InvariantCulture);

                // replace line text
                origLines[info.Index] = string.Join(",", cols.Select(Quote));
                remaining -= take;

                // update infos list: mutate by replacing existing entry with updated values
                // (we'll rebuild infos after all adjustments)
            }
            if (remaining > 0)
            {
                // couldn't fully decrease (shouldn't happen normally) — leave remainder (user must intervene)
            }
        }

        // Helper to increase into target HU (prefer existing line, else append new line cloned from a template)
        void IncreaseToTarget(string productId, string targetHu, int amount)
        {
            int remaining = amount;
            // find existing line for product+target
            var targetInfo = infos.FirstOrDefault(x => x.ProductId == productId && x.Hu == targetHu);
            if (targetInfo != null)
            {
                int lineQty = ToIntQuantity(targetInfo.Qty);
                decimal unitPrice = targetInfo.UnitPrice;
                decimal newQty = lineQty + remaining;
                decimal newNett = Round2(unitPrice * newQty);

                var cols = targetInfo.Cols;
                cols[6] = newQty.ToString("0.000", CultureInfo.InvariantCulture);
                cols[7] = newNett.ToString("0.00", CultureInfo.InvariantCulture);
                origLines[targetInfo.Index] = string.Join(",", cols.Select(Quote));
                return;
            }

            // No existing line: clone a template line for same product (prefer one with quantity >0), else pick any.
            var template = infos.FirstOrDefault(x => x.ProductId == productId);
            if (template != null)
            {
                var cols = new List<string>(template.Cols); // copy
                cols[15] = targetHu;
                cols[6] = remaining.ToString("0.000", CultureInfo.InvariantCulture);
                decimal unitPrice = template.UnitPrice;
                cols[7] = Round2(unitPrice * remaining).ToString("0.00", CultureInfo.InvariantCulture);
                var newLine = string.Join(",", cols.Select(Quote));
                origLines.Add(newLine);
                return;
            }

            // No template available: nothing to append (user action required)
        }

        // Perform transfers per product
        foreach (var pid in byProductSurplus.Keys.ToList())
        {
            if (!byProductDeficit.TryGetValue(pid, out var deficits)) continue;
            var surpluses = byProductSurplus[pid];

            // simple greedy: match first surplus with first deficit
            int sIdx = 0, dIdx = 0;
            while (sIdx < surpluses.Count && dIdx < deficits.Count)
            {
                var (sHu, sAmt) = surpluses[sIdx];
                var (dHu, dAmt) = deficits[dIdx];
                int transfer = Math.Min(sAmt, dAmt);

                // Apply transfer
                DecreaseFromSource(pid, sHu, transfer);
                IncreaseToTarget(pid, dHu, transfer);

                fixes.Add($"Transfer {transfer} of product {pid} from HU {sHu} -> HU {dHu}");

                // update lists
                sAmt -= transfer;
                dAmt -= transfer;
                if (sAmt == 0) sIdx++; else surpluses[sIdx] = (sHu, sAmt);
                if (dAmt == 0) dIdx++; else deficits[dIdx] = (dHu, dAmt);
            }

            // update dictionaries in case some remain; remaining unmatched will be left for user action
            byProductSurplus[pid] = surpluses.Skip(sIdx).ToList();
            byProductDeficit[pid] = deficits.Skip(dIdx).ToList();
        }

        // 6) persist modified fixed file (if any fixes done)
        if (fixes.Count > 0)
        {
            // ensure we atomically write: write to temp then move
            var temp = fixedFilePath + ".tmp";
            File.WriteAllLines(temp, origLines, Encoding.UTF8);
            File.Replace(temp, fixedFilePath, null);
        }

        // 7) recompute actual totals after attempted fixes
        // Rebuild infos and actual from updated file
        var updatedLines = File.ReadAllLines(fixedFilePath, Encoding.UTF8).ToList();
        var updatedInfos = new List<LineInfo>();
        for (int i = 0; i < updatedLines.Count; i++)
        {
            var raw = updatedLines[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cols = CsvSplit(raw);
            if (cols.Count == 0) continue;
            if (Unquote(cols[0]) != "2") continue;

            string productId = Unquote(Get(cols, 5));
            string hu = PadHu20(Unquote(Get(cols, 15)));
            decimal qty = ParseDec(Unquote(Get(cols, 6)));
            decimal nett = ParseDec(Unquote(Get(cols, 7)));
            decimal unitPrice = qty != 0 ? nett / qty : 0m;

            updatedInfos.Add(new LineInfo(i, cols, productId, hu, qty, nett, unitPrice));
        }

        var updatedActual = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var info in updatedInfos)
        {
            int q = ToIntQuantity(info.Qty);
            if (!updatedActual.TryGetValue(info.Hu, out var m)) { m = new Dictionary<string, int>(StringComparer.Ordinal); updatedActual[info.Hu] = m; }
            m.TryGetValue(info.ProductId, out int cur);
            m[info.ProductId] = cur + q;
        }

        // 8) produce verification report with statuses
        var sb = new StringBuilder();
        sb.AppendLine($"Verification report for '{Path.GetFileName(fixedFilePath)}' vs PDF '{Path.GetFileName(pdfPath)}'");
        sb.AppendLine();

        var hus2 = new HashSet<string>(expected.Keys, StringComparer.Ordinal);
        foreach (var k in updatedActual.Keys) hus2.Add(k);

        bool allGood = true;
        foreach (var hu in hus2.OrderBy(x => x))
        {
            sb.AppendLine($"HU: {hu}");
            var prodKeys = new HashSet<string>(StringComparer.Ordinal);
            if (expected.TryGetValue(hu, out var expMap)) foreach (var k in expMap.Keys) prodKeys.Add(k);
            if (updatedActual.TryGetValue(hu, out var actMap)) foreach (var k in actMap.Keys) prodKeys.Add(k);

            foreach (var pid in prodKeys.OrderBy(x => x))
            {
                int exp = expMap != null && expMap.TryGetValue(pid, out var e) ? e : 0;
                int act = actMap != null && actMap.TryGetValue(pid, out var a) ? a : 0;
                if (exp == act)
                {
                    sb.AppendLine($"  Product {pid}: expected={exp}, actual={act}  -- OK");
                }
                else
                {
                    allGood = false;
                    // check whether this mismatch was present originally and we attempted fixes
                    var originally = mismatches.ContainsKey((hu, pid));
                    var fixedNow = !originally ? false : (mismatches[(hu, pid)] != (act - exp));
                    // better: decide status based on whether any fix involved this PID and HU
                    bool involvedInFix = fixes.Any(f => f.Contains($"product {pid}") && (f.Contains(hu) || f.Contains("-> " + hu) || f.Contains(hu + " ->")));
                    string status = involvedInFix ? "-- MISMATCH (FIXED if you see 'OK' above else USER ACTION REQUIRED)" : "-- MISMATCH (USER ACTION REQUIRED)";
                    sb.AppendLine($"  Product {pid}: expected={exp}, actual={act}  {status}");
                }
            }

            sb.AppendLine();
        }

        if (fixes.Count > 0)
        {
            sb.AppendLine("Automated adjustments performed:");
            foreach (var fx in fixes) sb.AppendLine("  " + fx);
            sb.AppendLine();
        }

        sb.AppendLine(allGood ? "VERIFICATION OK: all HU/product totals match PDF." : "VERIFICATION FAILED: mismatches remain. See above.");
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

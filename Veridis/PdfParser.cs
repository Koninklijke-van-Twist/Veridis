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
    static readonly Regex RxInvoiceNumber = new(@"(?:(?:Invoice|Billing\s*Doc\.?|Billing\s*Document)\s*Number[:\s]+(?<v>\d{7,}))|(?<v>\d{7,})\s*(?:Invoice|Billing\s*Doc\.?|Billing\s*Document)\s*Number", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RxCustomerNumber = new(@"(?:Customer\s*Number[:\s]+(?<v>\d+))|(?<v>\d+)\s*Customer\s*Number", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Delivery Address Number can appear as squashed text; allow optional words and spacing:
    static readonly Regex RxDeliveryAddrNo = new(@"(?:Delivery\s*Address\s*Number[:\s]+(?<v>\d+))|(?<v>\d{6,})\s*Delivery\s*Address\s*Number", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RxVat = new(@"(?:VAT\s*Number|Customer\s*VAT\s*Number)[:\s]+(?<v>[A-Z0-9]+)|(?<v>[A-Z0-9]+)\s*(?:VAT\s*Number|Customer\s*VAT\s*Number)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string DetectCurrency(IEnumerable<string> lines)
        => lines.Any(l => l.Contains(" EUR ")) ? "EUR" : "EUR";


    // ---- Case Details rows (single line, stable) ----
    static readonly Regex RxCaseRow = new(
        @"^(?<hu>\d{10})\s+(?<delivery>\d+)\s+(?<item>[A-Z0-9]+)\s+(?<desc>.+?)\s+(?<country>[A-Z]{2})\s+(?<qty>\d+)\s*$",
        RegexOptions.Compiled);

    // ---- Invoice Detail: parse as a multi-line block ----
    // First (numeric) line of a detail block; allows optional "H" token after order no.
    static readonly Regex RxDetailStart = new(
    @"^(?<ord>[A-Z0-9]+)\s+(?:H\s+)?(?<itemno>\d+)\s+(?<ordered>[A-Z0-9]+)\s+(?<supplied>[A-Z0-9]+)\s+(?<qty>\d+)\s+0\s+(?<uoi>\d+)\s+(?<unitprice>\d+(?:\.\d{2})?)\s+(?<lineprice>\d+(?:\.\d{2})?)$",
    RegexOptions.Compiled);

    // The following appear within the next few lines after the start:
    static readonly Regex RxTariff = new(@"(?<code>\d{8,10})\s*Tariff\s*Code", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RxCoo = new(@"(?<coo>[A-Z]{2})\s*Country\s*of\s*Origin", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RxEccnUs = new(@"\bECCN\s*:\s*(?<v>[A-Z0-9\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RxEccnUk = new(@"\bECCN\s*\(UK\)\s*:\s*(?<v>Not on Control List|[A-Z0-9\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RxUnitWt = new(@"(?<w>\d+(?:\.\d+)?)\s*Qty\s*Sent\s*Net\s*Wt", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RxCpc = new(@"\bCPC\s*Code\s*[:\s]*(?<v>\d{4,8})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (InvoiceHeader Header, List<DetailLine> Details, List<CaseAlloc> Cases) Parse(string pdfPath)
    {
        using PdfDocument doc = PdfDocument.Open(pdfPath);
        InvoiceHeader header = new InvoiceHeader("", "", "", "EUR", "");
        List<DetailLine> details = new();
        List<CaseAlloc> cases = new();

        bool inDetails = false, inCases = false;

        foreach (Page page in doc.GetPages())
        {
            string text = ContentOrderTextExtractor.GetText(page);
            List<string> lines = text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            var twoLine = new List<string>(lines.Count * 2);
            twoLine.AddRange(lines);
            for (int idx = 0; idx + 1 < lines.Count; idx++)
            {
                twoLine.Add(lines[idx] + " " + lines[idx + 1]);
            }

            static string CleanAlnum(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "";
                var m = Regex.Match(s, @"[A-Z0-9]+", RegexOptions.IgnoreCase);
                return m.Success ? m.Value.ToUpperInvariant() : "";
            }

            // ---- Header extraction (first pages) ----
            if (string.IsNullOrEmpty(header.InvoiceNumber))
            {
                // ---- Header extraction (fill each field the first time we find it, on any page) ----
                header = header with
                {
                    CustomerNumber = string.IsNullOrEmpty(header.CustomerNumber)
                        ? MatchEither(twoLine, RxCustomerNumber, header.CustomerNumber) : header.CustomerNumber,

                    DeliveryAddressNumber = string.IsNullOrEmpty(header.DeliveryAddressNumber)
                        ? MatchEither(twoLine, RxDeliveryAddrNo, header.DeliveryAddressNumber) : header.DeliveryAddressNumber,

                    InvoiceNumber = string.IsNullOrEmpty(header.InvoiceNumber)
                        ? MatchEither(twoLine, RxInvoiceNumber, header.InvoiceNumber) : header.InvoiceNumber,

                    VatNumber = string.IsNullOrEmpty(header.VatNumber)
                        ? MatchEither(twoLine, RxVat, header.VatNumber) : header.VatNumber,

                    CurrencyCode = DetectCurrency(lines)  // keep this as-is
                };

                header = header with
                {
                    CustomerNumber = CleanAlnum(header.CustomerNumber),
                    DeliveryAddressNumber = CleanAlnum(header.DeliveryAddressNumber),
                    VatNumber = CleanAlnum(header.VatNumber)
                };
            }

            // ---- Section state ----
            if (lines.Any(l => l.Equals("Invoice Detail", StringComparison.OrdinalIgnoreCase))) { inDetails = true; inCases = false; }
            if (lines.Any(l => l.Equals("Case Details", StringComparison.OrdinalIgnoreCase))) { inCases = true; inDetails = false; }
            if (lines.Any(l => l.Equals("General Summary", StringComparison.OrdinalIgnoreCase))) { inDetails = false; inCases = false; }

            // ---- Parse details as blocks ----
            if (inDetails)
                details.AddRange(ParseInvoiceDetailsBlock(lines));

            // ---- Parse case allocations ----
            if (inCases)
            {
                foreach (string l in lines)
                {
                    if (l.StartsWith("Handling Unit", StringComparison.OrdinalIgnoreCase)) continue;
                    Match m = RxCaseRow.Match(l);
                    if (!m.Success) continue;

                    cases.Add(new CaseAlloc(
                        HandlingUnit: m.Groups["hu"].Value,
                        DeliveryNumber: m.Groups["delivery"].Value,
                        ProductId: m.Groups["item"].Value,
                        Description: m.Groups["desc"].Value.Trim(),
                        CountryOfOrigin: m.Groups["country"].Value,
                        Quantity: int.Parse(m.Groups["qty"].Value)
                    ));
                }
            }
        }

        return (header, details, cases);
    }

    // --- helpers ---

    private static string MatchEither(IEnumerable<string> lines, Regex rx, string fallback)
        => lines.Select(l => rx.Match(l))
                .Where(m => m.Success)
                .Select(m => m.Groups["v"].Value)
                .FirstOrDefault() ?? fallback;

    private static IEnumerable<DetailLine> ParseInvoiceDetailsBlock(List<string> lines)
    {
        var results = new List<DetailLine>();
        string lastOrder = ""; // carry forward

        for (int i = 0; i < lines.Count; i++)
        {
            var m = RxDetailStart.Match(lines[i]);
            if (!m.Success) continue;

            string ord = m.Groups["ord"].Value;
            if (ord.Equals("H", StringComparison.OrdinalIgnoreCase)) ord = lastOrder;
            if (!string.IsNullOrEmpty(ord)) lastOrder = ord;

            string productId = m.Groups["supplied"].Value;
            string uoi = m.Groups["uoi"].Value == "1" ? "1.000" : m.Groups["uoi"].Value;

            // Unit price; supplier TXT's Nett Value is EXTENDED → multiply later by qty
            decimal unitPrice = decimal.Parse(m.Groups["unitprice"].Value, System.Globalization.CultureInfo.InvariantCulture);
            int qty = int.Parse(m.Groups["qty"].Value);

            // description is next non-empty line
            string desc = "";
            int j = i + 1;
            while (j < lines.Count && string.IsNullOrWhiteSpace(lines[j])) j++;
            if (j < lines.Count) desc = lines[j].Trim();

            // scan ahead a small window for attrs
            string tariff = "", coo = "", eccnUs = "", eccnUk = "", unitWt = "", cpc = "";

            for (int k = j + 1; k < Math.Min(j + 20, lines.Count); k++)
            {
                var lt = lines[k];
                if (tariff == "") { var t = RxTariff.Match(lt); if (t.Success) tariff = t.Groups["code"].Value; }
                if (coo == "") { var c = RxCoo.Match(lt); if (c.Success) coo = c.Groups["coo"].Value; }
                if (eccnUs == "") { var e = RxEccnUs.Match(lt); if (e.Success) eccnUs = e.Groups["v"].Value; }
                if (eccnUk == "") { var e = RxEccnUk.Match(lt); if (e.Success) eccnUk = e.Groups["v"].Value.Trim(); }
                if (unitWt == "") { var w = RxUnitWt.Match(lt); if (w.Success) unitWt = CaseJoin.NormalizeWeight(w.Groups["w"].Value); }
                if (cpc == "")
                {
                    // normal inline “CPC Code: 3171000”
                    var p = RxCpc.Match(lt);
                    if (p.Success)
                    {
                        cpc = p.Groups["v"].Value;
                    }
                    else if (lt.IndexOf("CPC", StringComparison.OrdinalIgnoreCase) >= 0 && k + 1 < lines.Count)
                    {
                        var next = Regex.Match(lines[k + 1], @"\b(?<v>\d{4,8})\b");
                        if (next.Success) cpc = next.Groups["v"].Value;
                    }
                }
            }

            results.Add(new DetailLine(
                ProductId: productId,
                CustomerOrderNumber: ord,
                TariffCode: tariff,
                CountryOfOrigin: coo,
                Uoi: uoi,
                UnitNetWeight: unitWt,           // already normalised ("x.xxx KG" or "")
                EccnUs: string.IsNullOrEmpty(eccnUs) ? "EAR99" : eccnUs,
                EccnUk: string.IsNullOrEmpty(eccnUk) ? "Not on Control List" : eccnUk,
                CustomerPartNumber: "0",
                PartDescription: desc,
                UnitNettValue: unitPrice,        // UNIT price; exporter multiplies by qty
                CpcCode: cpc
            ));
        }
        return results;
    }

    private static string FormatKg(string numeric) =>
        $"{decimal.Parse(numeric, System.Globalization.CultureInfo.InvariantCulture):0.000} KG";
}

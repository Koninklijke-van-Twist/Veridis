using System.Globalization;
using System.Text;

namespace Veridis;

public record SupplierTxtHeader(
    string CustomerNumber,
    string DeliveryAddressNumber,
    string InvoiceNumber,
    string CurrencyCode,
    string VatNumber,
    decimal GrandTotal,
    decimal NetTotal
);

public record SupplierTxtDetail(
    string CustomerOrderNumber,
    string ProductId,          // Supplied Part Number
    decimal UnitPrice,         // derived: NettValue / PickQuantity (supplier NettValue is extended)
    string TariffCode,
    string CountryOfOrigin,
    string EccnUs,
    string EccnUk,
    string CustomerPartNumber,
    string PartDescription,
    string Uoi,
    string NetWeight,          // as printed in TXT (we’ll normalise in CaseJoin)
    string CpcCode
);

public static class SupplierTxtParser
{
    public static (SupplierTxtHeader Header, List<SupplierTxtDetail> Details) Parse(string txtPath)
    {
        var lines = File.ReadAllLines(txtPath, Encoding.UTF8).ToList();
        var header = ParseHeader(lines);
        var details = ParseDetails(lines);
        return (header, details);
    }

    private static SupplierTxtHeader ParseHeader(List<string> lines)
    {
        // Find the first row whose first column is "1"
        foreach (var line in lines)
        {
            var cols = CsvSplit(line);
            if (cols.Count == 0) continue;
            if (cols[0] != "1") continue;

            // "1","CustomerNumber","DeliveryAddressNumber","InvoiceNumber","GrandTotal",...,"NetTotal","...","Currency","...","NetTotal_again",...,"VAT","NetTotal_again"
            string customer = Get(cols, 1);
            string delivery = Get(cols, 2);
            string invoice = Get(cols, 3);
            decimal grand = ParseDec(Get(cols, 4));
            // The first NetTotal slot in your sample is col 7 (0-based: 7)
            decimal net = ParseDec(Get(cols, 7));
            string currency = Get(cols, 9); // "EUR"
            string vat = Get(cols, 13);

            return new SupplierTxtHeader(
                CustomerNumber: CleanAlnum(customer),
                DeliveryAddressNumber: CleanAlnum(delivery),
                InvoiceNumber: CleanAlnum(invoice),
                CurrencyCode: currency,
                VatNumber: CleanAlnum(vat),
                GrandTotal: grand,
                NetTotal: net
            );
        }

        // Fallback empty
        return new SupplierTxtHeader("", "", "", "EUR", "", 0m, 0m);
    }

    private static List<SupplierTxtDetail> ParseDetails(List<string> lines)
    {
        var result = new List<SupplierTxtDetail>();

        foreach (var line in lines)
        {
            var cols = CsvSplit(line);
            if (cols.Count == 0) continue;
            if (cols[0] != "2") continue;

            // Based on your “Record Type 2” legend:
            // 0: "2"
            // 1 cust, 2 delivAddr, 3 invoice, 4 customer order, 5 supplied part, 6 qty, 7 nett value (extended), 8 user text,
            // 9 currency, 10 order type, 11 exchange, 12 programming, 13 tariff, 14 coo, 15 HU (can be "A/B/..."), 16 eccnUS, 17 eccnUK,
            // 18 cust part, 19 desc, 20 uoi, 21 net weight, 22 cpc
            string orderNo = Get(cols, 4);
            string productId = Get(cols, 5);
            decimal qty = ParseDec(Get(cols, 6));
            decimal nettExt = ParseDec(Get(cols, 7));
            decimal unit = qty == 0 ? 0 : Math.Round(nettExt / qty, 4, MidpointRounding.AwayFromZero);

            var detail = new SupplierTxtDetail(
                CustomerOrderNumber: orderNo,
                ProductId: productId,
                UnitPrice: unit,
                TariffCode: Get(cols, 13),
                CountryOfOrigin: Get(cols, 14),
                EccnUs: Get(cols, 16),
                EccnUk: Get(cols, 17),
                CustomerPartNumber: Get(cols, 18),
                PartDescription: Get(cols, 19),
                Uoi: Get(cols, 20),
                NetWeight: Get(cols, 21),
                CpcCode: Get(cols, 22)
            );

            // NOTE: we deliberately ignore the HU column (index 15) because it may contain grouped HUs like "A/B/..."
            // We will replace HUs using the PDF cases.
            result.Add(detail);
        }

        return result;
    }

    // --- helpers ---

    private static string Get(List<string> cols, int idx) => idx < cols.Count ? cols[idx] : "";

    private static List<string> CsvSplit(string line)
    {
        // Simple CSV split for your quoted rows
        var res = new List<string>();
        if (string.IsNullOrEmpty(line)) return res;

        int i = 0; var sb = new StringBuilder(); bool inQuotes = false;
        void Emit() { res.Add(sb.ToString()); sb.Clear(); }

        while (i < line.Length)
        {
            char ch = line[i++];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i < line.Length && line[i] == '"') { sb.Append('"'); i++; } // escaped quote
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else
            {
                if (ch == '"') inQuotes = true;
                else if (ch == ',') Emit();
                else sb.Append(ch);
            }
        }
        Emit();
        return res;
    }

    private static decimal ParseDec(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0m;
        s = s.Trim().Replace(" ", "").Replace(",", "."); // accept 2,460 as 2.460
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
    }

    private static string CleanAlnum(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var m = System.Text.RegularExpressions.Regex.Match(s, @"[A-Z0-9]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Value.ToUpperInvariant() : "";
    }
}

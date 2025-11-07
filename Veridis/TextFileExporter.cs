using System.Globalization;
using System.Text;
using Veridis;

public sealed class InvoiceTotals
{
    public decimal NetTotal { get; init; }       // sum of all RT2 extended values
    public decimal GrandTotal { get; init; }     // from PDF "Grand/Invoice Total" or Net+Other
    public decimal OtherCharges { get; init; }   // usually carriage/other; GrandTotal - NetTotal
}

public static class TextFileExporter
{
    public static void Export(string path, InvoiceHeader header, IEnumerable<CaseRecord2> rows, InvoiceTotals totals, bool includeLegend = true)
    {
        StringBuilder sb = new StringBuilder();

        if (includeLegend)
        {
            sb.AppendLine("Record Type 1,Customer Number,Delivery Address Number,Invoice Number,Grand Total Value,,,Nett Value,,Currency Code,,Sub Total2,Total VAT Value,VAT Number,Sub Total3,,,,,,,,");
            sb.AppendLine("Record Type 2,Customer Number,Delivery Address Number,Invoice Number,Customer Order Number,Supplied Part Number,Pick Quantity,Nett Value,User Text,Currency Code,Order Type,Exchange Value,Programming Charge,Tarrif Code,Country of Origin,HU,ECCN US,ECCN UK,Customer Part Number,Part Description,UOI(Unit of Issue),Net Weight,CPC Code");
            sb.AppendLine("Record Type 3,Customer Number,,Invoice Number,OTHERS or CAR CHG,,,Nett Value,,,,,,,,,,,,,,,");
        }

        // --- Record Type 1 (header/totals) ---
        // Matches your supplier’s column order. SubTotal2 & SubTotal3 mirror NetTotal.
        string[] rt1 = new[]
        {
            "1",
            header.CustomerNumber,
            header.DeliveryAddressNumber,
            header.InvoiceNumber,
            totals.GrandTotal.ToString("0.00", CultureInfo.InvariantCulture),
            "0", "0",
            totals.NetTotal.ToString("0.00", CultureInfo.InvariantCulture),
            "0",
            header.CurrencyCode,
            "0",
            totals.NetTotal.ToString("0.00", CultureInfo.InvariantCulture), // Sub Total2
            "0",                                                            // Total VAT Value (supplier sample shows 0)
            header.VatNumber ?? "",
            totals.NetTotal.ToString("0.00", CultureInfo.InvariantCulture), // Sub Total3
            "", "", "", "", "", "", "", "" // Required for import
        };
        sb.AppendLine(ToCsvRow(rt1));

        // --- Record Type 2 rows (detail per Handling Unit) ---
        foreach (CaseRecord2 r in rows)
        {
            // Supplier TXT expects extended value, not unit price
            decimal extended = Math.Round(r.UnitNettValue * r.PickQuantity, 2, MidpointRounding.AwayFromZero);

            string[] cells = new[]
            {
                "2",                                // Record Type 2 row
                r.CustomerNumber,
                r.DeliveryAddressNumber,
                r.InvoiceNumber,
                r.CustomerOrderNumber,
                r.SuppliedPartNumber,
                r.PickQuantity.ToString("0.000", CultureInfo.InvariantCulture),
                extended.ToString("0.00", CultureInfo.InvariantCulture),   // <-- extended nett value
                "0",                                // User Text (supplier uses "0")
                r.CurrencyCode,
                r.OrderType,
                r.ExchangeValue,                    // usually "0"
                r.ProgrammingCharge,                // usually "0"
                r.TariffCode,
                r.CountryOfOrigin,
                r.HuPadded20,                       // should already be 20-digit padded
                r.EccnUs,                           // e.g., "EAR99"
                r.EccnUk,                           // e.g., "Not on Control List"
                r.CustomerPartNumber,               // often "0"
                r.PartDescription,
                r.Uoi,                              // e.g., "1.000"
                r.NetWeight,                        // e.g., "0.600 KG"
                r.CpcCode                           // if you have it; else ""
            };

            sb.AppendLine(ToCsvRow(cells));
        }

        // --- Record Type 3 (others / carriage charge) ---
        if (totals.OtherCharges > 0.000m)
        {
            string[] rt3 = new[]
            {
                "3",
                header.CustomerNumber,
                "0",                               // sample shows 0 for delivery address no.
                header.InvoiceNumber,
                "OTHERS or CAR CHG",
                "0", "0",
                totals.OtherCharges.ToString("0.00", CultureInfo.InvariantCulture), // Nett Value
                "0","0","0","0","0","0","0",                                         // rest are zeros in sample
                "", "", "", "", "", "", "", "" // Required for import
            };
            sb.AppendLine(ToCsvRow(rt3));
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static string ToCsvRow(IEnumerable<string> cells) => string.Join(",", cells.Select(QuoteCsv));
    private static string QuoteCsv(string s) { s ??= ""; s = s.Replace("\"", "\"\""); return $"\"{s}\""; }
}

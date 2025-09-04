using System.Text.RegularExpressions;
using Veridis;

public static class CaseJoin
{
    public static IEnumerable<CaseRecord2> BuildCaseRecords(
        InvoiceHeader H,
        List<DetailLine> pdfDetails,
        List<CaseAlloc> cases,
        List<SupplierTxtDetail>? txtDetails = null)
    {
        var byItemPdf = pdfDetails.GroupBy(d => d.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var byItemTxt = (txtDetails ?? new()).GroupBy(d => d.ProductId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var c in cases)
        {
            SupplierTxtDetail? td = null;
            byItemTxt.TryGetValue(c.ProductId, out var txtList);

            // Prefer TXT detail with the best description match (fallback: first)
            if (txtList is not null && txtList.Count > 0)
            {
                td = txtList.OrderByDescending(x => Score(Norm(c.Description), Norm(x.PartDescription))).First();
            }

            // If no TXT detail, fall back to PDF detail
            DetailLine? pd = null;
            if (td is null && byItemPdf.TryGetValue(c.ProductId, out var pdfList) && pdfList.Count > 0)
                pd = PickBestPdfDetail(c, pdfList);

            // unify values (prefer TXT; fallback to PDF)
            string orderNo = (td?.CustomerOrderNumber ?? pd?.CustomerOrderNumber ?? "").Trim();
            if (string.Equals(orderNo, "H", StringComparison.OrdinalIgnoreCase)) orderNo = "";

            decimal unitPrice = td?.UnitPrice ?? pd?.UnitNettValue ?? 0m;
            string tariff = td?.TariffCode ?? pd?.TariffCode ?? "";
            string coo = c.CountryOfOrigin; // always from cases
            string eccnUs = EmptyOr(td?.EccnUs, pd?.EccnUs, "EAR99");
            string eccnUk = EmptyOr(td?.EccnUk, pd?.EccnUk, "Not on Control List");
            string custPart = td?.CustomerPartNumber ?? pd?.CustomerPartNumber ?? "0";
            string desc = td?.PartDescription ?? pd?.PartDescription ?? c.Description;
            string uoi = td?.Uoi ?? pd?.Uoi ?? "1.000";
            string weight = NormalizeWeight(td?.NetWeight ?? pd?.UnitNetWeight);
            string cpc = td?.CpcCode ?? pd?.CpcCode ?? ""; // <- CPC from TXT when present

            yield return new CaseRecord2(
                CustomerNumber: H.CustomerNumber,
                DeliveryAddressNumber: H.DeliveryAddressNumber,
                InvoiceNumber: H.InvoiceNumber,
                CustomerOrderNumber: orderNo,
                SuppliedPartNumber: c.ProductId,
                PickQuantity: c.Quantity,
                UnitNettValue: unitPrice,      // exporter multiplies by qty
                UserText: "0",
                CurrencyCode: H.CurrencyCode,
                OrderType: "Standard Order",
                ExchangeValue: "0",
                ProgrammingCharge: "0",
                TariffCode: tariff,
                CountryOfOrigin: coo,
                HuPadded20: PadHu20(c.HandlingUnit),
                EccnUs: eccnUs,
                EccnUk: eccnUk,
                CustomerPartNumber: custPart,
                PartDescription: desc,
                Uoi: uoi,
                NetWeight: weight,
                CpcCode: cpc
            );
        }
    }

    private static DetailLine PickBestPdfDetail(CaseAlloc c, List<DetailLine> list)
    {
        if (list.Count == 1) return list[0];
        string normCase = Norm(c.Description);
        return list.OrderByDescending(d => Score(normCase, Norm(d.PartDescription)))
                   .ThenByDescending(d => string.Equals(d.CountryOfOrigin, c.CountryOfOrigin, StringComparison.OrdinalIgnoreCase))
                   .First();
    }

    private static string EmptyOr(params string?[] vals)
    {
        foreach (var v in vals)
            if (!string.IsNullOrWhiteSpace(v)) return v!;
        return "";
    }

    private static string Norm(string s) => new string(s.Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
    private static int Score(string a, string b) => a.Length == 0 || b.Length == 0 ? 0 : (b.StartsWith(a[..Math.Min(a.Length, 12)]) ? 2 : a.Split(' ').Intersect(b.Split(' ')).Count());

    public static string NormalizeWeight(string? rawKg)
    {
        if (string.IsNullOrWhiteSpace(rawKg)) return "";
        var m = Regex.Match(rawKg, @"(?<n>\d+(?:[.,]\d+)?)");
        if (!m.Success) return "";
        var n = m.Groups["n"].Value.Replace(',', '.');
        return decimal.TryParse(n, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? $"{v:0.000} KG"
            : "";
    }

    private static string PadHu20(string huDigits)
    {
        var d = new string((huDigits ?? "").Where(char.IsDigit).ToArray());
        return d.PadLeft(20, '0');
    }
}

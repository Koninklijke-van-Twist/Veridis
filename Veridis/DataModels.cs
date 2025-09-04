namespace Veridis
{
    public record InvoiceHeader(
        string CustomerNumber,
        string DeliveryAddressNumber,
        string InvoiceNumber,
        string CurrencyCode,
        string VatNumber
    );

    public record DetailLine(
        string ProductId,
        string CustomerOrderNumber,
        string TariffCode,
        string CountryOfOrigin,
        string Uoi,
        string UnitNetWeight,     
        string EccnUs,
        string EccnUk,
        string CustomerPartNumber, 
        string PartDescription,
        decimal UnitNettValue,
        string CpcCode
    );

    public record CaseAlloc( 
        string HandlingUnit,      
        string DeliveryNumber,
        string ProductId,
        string Description,
        string CountryOfOrigin,
        int Quantity
    );

    public record CaseRecord2(
        string CustomerNumber,
        string DeliveryAddressNumber,
        string InvoiceNumber,
        string CustomerOrderNumber,
        string SuppliedPartNumber,
        int PickQuantity,
        decimal UnitNettValue,
        string UserText,           
        string CurrencyCode,
        string OrderType,          
        string ExchangeValue,      
        string ProgrammingCharge, 
        string TariffCode,
        string CountryOfOrigin,
        string HuPadded20,
        string EccnUs,
        string EccnUk,
        string CustomerPartNumber,
        string PartDescription,
        string Uoi,
        string NetWeight,         
        string CpcCode            
    );

}

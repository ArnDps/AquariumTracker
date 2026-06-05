namespace ADAqua.Domain;

public sealed class WaterParameters
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime MeasuredAt { get; set; } = DateTime.Now;
    public DateTime MeasuredOnDate
    {
        get => MeasuredAt.Date;
        set => MeasuredAt = value.Date.Add(MeasuredAt.TimeOfDay);
    }

    public decimal? AmmoniaMgPerLiter { get; set; }
    public decimal? NitritesMgPerLiter { get; set; }
    public decimal? NitratesMgPerLiter { get; set; }
    public decimal? Ph { get; set; }
    public decimal? Gh { get; set; }
    public decimal? Kh { get; set; }
    public decimal? TemperatureCelsius { get; set; }
    public string Notes { get; set; } = string.Empty;
}

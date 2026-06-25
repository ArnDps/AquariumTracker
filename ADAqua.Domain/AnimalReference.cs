namespace ADAqua.Domain;

public enum AnimalReferenceEnvironment
{
    FreshwaterTropical,
    Marine
}

public enum AnimalReferenceGroup
{
    Fish,
    Shrimp,
    Snail,
    Other
}

public sealed class AnimalReference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AnimalReferenceEnvironment Environment { get; set; } = AnimalReferenceEnvironment.FreshwaterTropical;
    public AnimalReferenceGroup Group { get; set; } = AnimalReferenceGroup.Fish;
    public string CommonName { get; set; } = string.Empty;
    public string CommonNameFr { get; set; } = string.Empty;
    public string CommonNameEn { get; set; } = string.Empty;
    public string CommonNameDe { get; set; } = string.Empty;
    public string ScientificName { get; set; } = string.Empty;
    public decimal? PhMin { get; set; }
    public decimal? PhMax { get; set; }
    public decimal? GhMin { get; set; }
    public decimal? GhMax { get; set; }
    public decimal? KhMin { get; set; }
    public decimal? KhMax { get; set; }
    public decimal? TemperatureMin { get; set; }
    public decimal? TemperatureMax { get; set; }
    public decimal? AmmoniaMin { get; set; }
    public decimal? AmmoniaMax { get; set; }
    public decimal? NitritesMin { get; set; }
    public decimal? NitritesMax { get; set; }
    public decimal? NitratesMin { get; set; }
    public decimal? NitratesMax { get; set; }
    public int? VolumeMinLiters { get; set; }
    public string Behavior { get; set; } = string.Empty;
    public string Compatibility { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
}

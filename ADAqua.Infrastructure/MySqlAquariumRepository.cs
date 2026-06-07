using ADAqua.Domain;
using MySqlConnector;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace ADAqua.Infrastructure;

public sealed class MySqlAquariumRepository(string connectionString) : IAquariumRepository
{
    private static readonly TimeSpan WebRequestTimeout = TimeSpan.FromSeconds(35);
    private const int WebRequestMaxAttempts = 3;
    private static readonly TimeSpan WebRetryDelay = TimeSpan.FromMilliseconds(800);
    private const int MinimumPlantEssentialParameterCount = 4;
    private const int MinimumAnimalEssentialParameterCount = 4;
    private const string FreshwaterAquariumFishListUrl = "https://en.wikipedia.org/wiki/List_of_freshwater_aquarium_fish_species";

    private static readonly HashSet<string> ChildTables =
    [
        "WaterMeasurements",
        "AquariumPlants",
        "PopulationMembers",
        "AquariumInterventions"
    ];

    public async Task<IReadOnlyList<Aquarium>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureAquariumChildSchemaUpgradedAsync(connection, cancellationToken);

        var aquariums = new Dictionary<Guid, Aquarium>();
        await LoadAquariumsAsync(connection, aquariums, cancellationToken);
        await LoadMeasurementsAsync(connection, aquariums, cancellationToken);
        await LoadPlantsAsync(connection, aquariums, cancellationToken);
        await LoadPopulationAsync(connection, aquariums, cancellationToken);
        await LoadInterventionsAsync(connection, aquariums, cancellationToken);

        return aquariums.Values.OrderBy(aquarium => aquarium.Name).ToList();
    }

    public async Task SaveAsync(Aquarium aquarium, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureAquariumChildSchemaUpgradedAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO Aquariums (Id, Name, VolumeLiters, WaterType, StartedOn, Notes)
            VALUES (@Id, @Name, @VolumeLiters, @WaterType, @StartedOn, @Notes)
            ON DUPLICATE KEY UPDATE
                Name = VALUES(Name),
                VolumeLiters = VALUES(VolumeLiters),
                WaterType = VALUES(WaterType),
                StartedOn = VALUES(StartedOn),
                Notes = VALUES(Notes);
            """,
            cancellationToken,
            Parameter("@Id", aquarium.Id.ToString()),
            Parameter("@Name", aquarium.Name),
            Parameter("@VolumeLiters", aquarium.VolumeLiters),
            Parameter("@WaterType", aquarium.WaterType),
            Parameter("@StartedOn", aquarium.StartedOn.ToDateTime(TimeOnly.MinValue)),
            Parameter("@Notes", aquarium.Notes));

        await SaveChildrenAsync(connection, transaction, aquarium, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid aquariumId, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await ExecuteAsync(
            connection,
            transaction: null,
            "DELETE FROM Aquariums WHERE Id = @Id;",
            cancellationToken,
            Parameter("@Id", aquariumId.ToString()));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var statement in MySqlSchema.Statements)
        {
            await using var command = new MySqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureAquariumChildSchemaUpgradedAsync(connection, cancellationToken);
        await EnsurePlantReferenceSchemaUpgradedAsync(connection, cancellationToken);
        await EnsurePlantReferenceImportCandidateSchemaAsync(connection, cancellationToken);
        await EnsureAnimalReferenceSchemaUpgradedAsync(connection, cancellationToken);
        await EnsureAnimalReferenceImportCandidateSchemaAsync(connection, cancellationToken);
        await EnsurePlantReferencesSeededAsync(connection, cancellationToken);
        await EnsureAnimalReferencesSeededAsync(connection, cancellationToken);
        await NormalizeExistingCommonNamesAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<PlantReference>> GetPlantReferencesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsurePlantReferenceSchemaUpgradedAsync(connection, cancellationToken);
        await EnsurePlantReferencesSeededAsync(connection, cancellationToken);

        var references = new List<PlantReference>();
        await using var command = new MySqlCommand(
            """
            SELECT Id, Environment, CommonName, CommonNameFr, CommonNameEn, CommonNameDe, ScientificName, PhMin, PhMax, GhMin, GhMax, KhMin, KhMax,
                   TemperatureMin, TemperatureMax, AmmoniaMin, AmmoniaMax, NitritesMin, NitritesMax,
                   NitratesMin, NitratesMax, VolumeMinLiters, LightNeed, Co2Need, FertilizationNeed,
                   GrowthSpeed, RecommendedPlacement, Behavior, Compatibility, SourceUrl
            FROM PlantReferences
            ORDER BY Environment, CommonName;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            references.Add(new PlantReference
            {
                Id = ReadGuid(reader, 0),
                Environment = Enum.Parse<PlantReferenceEnvironment>(reader.GetString(1)),
                CommonName = reader.GetString(2),
                CommonNameFr = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                CommonNameEn = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                CommonNameDe = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                ScientificName = reader.GetString(6),
                PhMin = ReadNullableDecimal(reader, 7),
                PhMax = ReadNullableDecimal(reader, 8),
                GhMin = ReadNullableDecimal(reader, 9),
                GhMax = ReadNullableDecimal(reader, 10),
                KhMin = ReadNullableDecimal(reader, 11),
                KhMax = ReadNullableDecimal(reader, 12),
                TemperatureMin = ReadNullableDecimal(reader, 13),
                TemperatureMax = ReadNullableDecimal(reader, 14),
                AmmoniaMin = ReadNullableDecimal(reader, 15),
                AmmoniaMax = ReadNullableDecimal(reader, 16),
                NitritesMin = ReadNullableDecimal(reader, 17),
                NitritesMax = ReadNullableDecimal(reader, 18),
                NitratesMin = ReadNullableDecimal(reader, 19),
                NitratesMax = ReadNullableDecimal(reader, 20),
                VolumeMinLiters = reader.IsDBNull(21) ? null : reader.GetInt32(21),
                LightNeed = reader.GetString(22),
                Co2Need = reader.GetString(23),
                FertilizationNeed = reader.GetString(24),
                GrowthSpeed = reader.GetString(25),
                RecommendedPlacement = reader.GetString(26),
                Behavior = reader.GetString(27),
                Compatibility = reader.GetString(28),
                SourceUrl = reader.GetString(29)
            });
        }

        return references;
    }

    public async Task<IReadOnlyList<AnimalReference>> GetAnimalReferencesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureAnimalReferenceSchemaUpgradedAsync(connection, cancellationToken);
        await EnsureAnimalReferencesSeededAsync(connection, cancellationToken);

        var references = new List<AnimalReference>();
        await using var command = new MySqlCommand(
            """
            SELECT Id, Environment, CommonName, CommonNameFr, CommonNameEn, CommonNameDe, ScientificName, PhMin, PhMax, GhMin, GhMax, KhMin, KhMax,
                   TemperatureMin, TemperatureMax, AmmoniaMin, AmmoniaMax, NitritesMin, NitritesMax,
                   NitratesMin, NitratesMax, VolumeMinLiters, Behavior, Compatibility, SourceUrl
            FROM AnimalReferences
            ORDER BY Environment, CommonName;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            references.Add(new AnimalReference
            {
                Id = ReadGuid(reader, 0),
                Environment = Enum.Parse<AnimalReferenceEnvironment>(reader.GetString(1)),
                CommonName = reader.GetString(2),
                CommonNameFr = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                CommonNameEn = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                CommonNameDe = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                ScientificName = reader.GetString(6),
                PhMin = ReadNullableDecimal(reader, 7),
                PhMax = ReadNullableDecimal(reader, 8),
                GhMin = ReadNullableDecimal(reader, 9),
                GhMax = ReadNullableDecimal(reader, 10),
                KhMin = ReadNullableDecimal(reader, 11),
                KhMax = ReadNullableDecimal(reader, 12),
                TemperatureMin = ReadNullableDecimal(reader, 13),
                TemperatureMax = ReadNullableDecimal(reader, 14),
                AmmoniaMin = ReadNullableDecimal(reader, 15),
                AmmoniaMax = ReadNullableDecimal(reader, 16),
                NitritesMin = ReadNullableDecimal(reader, 17),
                NitritesMax = ReadNullableDecimal(reader, 18),
                NitratesMin = ReadNullableDecimal(reader, 19),
                NitratesMax = ReadNullableDecimal(reader, 20),
                VolumeMinLiters = reader.IsDBNull(21) ? null : reader.GetInt32(21),
                Behavior = reader.GetString(22),
                Compatibility = reader.GetString(23),
                SourceUrl = reader.GetString(24)
            });
        }

        return references;
    }

    private static async Task LoadAquariumsAsync(MySqlConnection connection, Dictionary<Guid, Aquarium> aquariums, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("SELECT Id, Name, VolumeLiters, WaterType, StartedOn, Notes FROM Aquariums;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var aquarium = new Aquarium
            {
                Id = ReadGuid(reader, 0),
                Name = reader.GetString(1),
                VolumeLiters = reader.GetDecimal(2),
                WaterType = reader.GetString(3),
                StartedOn = DateOnly.FromDateTime(reader.GetDateTime(4)),
                Notes = reader.GetString(5)
            };

            aquariums[aquarium.Id] = aquarium;
        }
    }

    private static async Task LoadMeasurementsAsync(MySqlConnection connection, Dictionary<Guid, Aquarium> aquariums, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("SELECT Id, AquariumId, MeasuredAt, Ammonia, Nitrites, Nitrates, Ph, Gh, Kh, TemperatureCelsius, Notes FROM WaterMeasurements ORDER BY AquariumId, MeasuredAt DESC;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken) && aquariums.TryGetValue(ReadGuid(reader, 1), out var aquarium))
        {
            aquarium.Measurements.Add(new WaterParameters
            {
                Id = ReadGuid(reader, 0),
                MeasuredAt = reader.GetDateTime(2),
                AmmoniaMgPerLiter = ReadNullableDecimal(reader, 3),
                NitritesMgPerLiter = ReadNullableDecimal(reader, 4),
                NitratesMgPerLiter = ReadNullableDecimal(reader, 5),
                Ph = ReadNullableDecimal(reader, 6),
                Gh = ReadNullableDecimal(reader, 7),
                Kh = ReadNullableDecimal(reader, 8),
                TemperatureCelsius = ReadNullableDecimal(reader, 9),
                Notes = reader.GetString(10)
            });
        }
    }

    private static async Task LoadPlantsAsync(MySqlConnection connection, Dictionary<Guid, Aquarium> aquariums, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("SELECT Id, AquariumId, AddedOn, CommonName, ScientificName, GrowthSpeed, LightNeed, Notes FROM AquariumPlants;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken) && aquariums.TryGetValue(ReadGuid(reader, 1), out var aquarium))
        {
            aquarium.Plants.Add(new AquariumPlant
            {
                Id = ReadGuid(reader, 0),
                AddedOn = reader.GetDateTime(2).Date,
                CommonName = reader.GetString(3),
                ScientificName = reader.GetString(4),
                GrowthSpeed = Enum.Parse<PlantGrowthSpeed>(reader.GetString(5)),
                LightNeed = reader.GetString(6),
                Notes = reader.GetString(7)
            });
        }
    }

    private static async Task LoadPopulationAsync(MySqlConnection connection, Dictionary<Guid, Aquarium> aquariums, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("SELECT Id, AquariumId, AddedOn, SpeciesName, CommonName, Type, Quantity, Notes FROM PopulationMembers;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken) && aquariums.TryGetValue(ReadGuid(reader, 1), out var aquarium))
        {
            aquarium.Population.Add(new PopulationMember
            {
                Id = ReadGuid(reader, 0),
                AddedOn = reader.GetDateTime(2).Date,
                SpeciesName = reader.GetString(3),
                CommonName = reader.GetString(4),
                Type = Enum.Parse<PopulationType>(reader.GetString(5)),
                Quantity = reader.GetInt32(6),
                Notes = reader.GetString(7)
            });
        }
    }

    private static async Task LoadInterventionsAsync(MySqlConnection connection, Dictionary<Guid, Aquarium> aquariums, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            SELECT Id, AquariumId, OccurredAt, Type, ProductName, ProductQuantity, WaterVolumeLiters, WaterPercentage,
                   PopulationChangeReason, PopulationChangeCount, Notes
            FROM AquariumInterventions
            ORDER BY AquariumId, OccurredAt DESC;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken) && aquariums.TryGetValue(ReadGuid(reader, 1), out var aquarium))
        {
            aquarium.Interventions.Add(new AquariumIntervention
            {
                Id = ReadGuid(reader, 0),
                OccurredAt = reader.GetDateTime(2),
                Type = Enum.Parse<InterventionType>(reader.GetString(3)),
                ProductName = reader.GetString(4),
                ProductQuantity = reader.GetString(5),
                WaterVolumeLiters = ReadNullableDecimal(reader, 6),
                WaterPercentage = ReadNullableDecimal(reader, 7),
                PopulationChangeReason = reader.GetString(8),
                PopulationChangeCount = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                Notes = reader.GetString(10)
            });
        }
    }

    private static async Task SaveChildrenAsync(MySqlConnection connection, MySqlTransaction transaction, Aquarium aquarium, CancellationToken cancellationToken)
    {
        foreach (var measurement in aquarium.Measurements)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO WaterMeasurements (Id, AquariumId, MeasuredAt, Ammonia, Nitrites, Nitrates, Ph, Gh, Kh, TemperatureCelsius, Notes)
                VALUES (@Id, @AquariumId, @MeasuredAt, @Ammonia, @Nitrites, @Nitrates, @Ph, @Gh, @Kh, @TemperatureCelsius, @Notes)
                ON DUPLICATE KEY UPDATE
                    MeasuredAt = VALUES(MeasuredAt),
                    Ammonia = VALUES(Ammonia),
                    Nitrites = VALUES(Nitrites),
                    Nitrates = VALUES(Nitrates),
                    Ph = VALUES(Ph),
                    Gh = VALUES(Gh),
                    Kh = VALUES(Kh),
                    TemperatureCelsius = VALUES(TemperatureCelsius),
                    Notes = VALUES(Notes);
                """,
                cancellationToken,
                Parameter("@Id", measurement.Id.ToString()),
                Parameter("@AquariumId", aquarium.Id.ToString()),
                Parameter("@MeasuredAt", measurement.MeasuredAt),
                Parameter("@Ammonia", measurement.AmmoniaMgPerLiter),
                Parameter("@Nitrites", measurement.NitritesMgPerLiter),
                Parameter("@Nitrates", measurement.NitratesMgPerLiter),
                Parameter("@Ph", measurement.Ph),
                Parameter("@Gh", measurement.Gh),
                Parameter("@Kh", measurement.Kh),
                Parameter("@TemperatureCelsius", measurement.TemperatureCelsius),
                Parameter("@Notes", measurement.Notes));
        }

        await DeleteMissingChildrenAsync(connection, transaction, "WaterMeasurements", aquarium.Id, aquarium.Measurements.Select(measurement => measurement.Id), cancellationToken);

        foreach (var plant in aquarium.Plants)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO AquariumPlants (Id, AquariumId, AddedOn, CommonName, ScientificName, GrowthSpeed, LightNeed, Notes)
                VALUES (@Id, @AquariumId, @AddedOn, @CommonName, @ScientificName, @GrowthSpeed, @LightNeed, @Notes)
                ON DUPLICATE KEY UPDATE
                    AddedOn = VALUES(AddedOn),
                    CommonName = VALUES(CommonName),
                    ScientificName = VALUES(ScientificName),
                    GrowthSpeed = VALUES(GrowthSpeed),
                    LightNeed = VALUES(LightNeed),
                    Notes = VALUES(Notes);
                """,
                cancellationToken,
                Parameter("@Id", plant.Id.ToString()),
                Parameter("@AquariumId", aquarium.Id.ToString()),
                Parameter("@AddedOn", NormalizeDateOrToday(plant.AddedOn)),
                Parameter("@CommonName", CapitalizeFirstLetter(plant.CommonName, 120)),
                Parameter("@ScientificName", plant.ScientificName),
                Parameter("@GrowthSpeed", plant.GrowthSpeed.ToString()),
                Parameter("@LightNeed", plant.LightNeed),
                Parameter("@Notes", plant.Notes));
        }

        await DeleteMissingChildrenAsync(connection, transaction, "AquariumPlants", aquarium.Id, aquarium.Plants.Select(plant => plant.Id), cancellationToken);

        foreach (var member in aquarium.Population)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO PopulationMembers (Id, AquariumId, AddedOn, SpeciesName, CommonName, Type, Quantity, Notes)
                VALUES (@Id, @AquariumId, @AddedOn, @SpeciesName, @CommonName, @Type, @Quantity, @Notes)
                ON DUPLICATE KEY UPDATE
                    AddedOn = VALUES(AddedOn),
                    SpeciesName = VALUES(SpeciesName),
                    CommonName = VALUES(CommonName),
                    Type = VALUES(Type),
                    Quantity = VALUES(Quantity),
                    Notes = VALUES(Notes);
                """,
                cancellationToken,
                Parameter("@Id", member.Id.ToString()),
                Parameter("@AquariumId", aquarium.Id.ToString()),
                Parameter("@AddedOn", NormalizeDateOrToday(member.AddedOn)),
                Parameter("@SpeciesName", member.SpeciesName),
                Parameter("@CommonName", CapitalizeFirstLetter(member.CommonName, 120)),
                Parameter("@Type", member.Type.ToString()),
                Parameter("@Quantity", member.Quantity),
                Parameter("@Notes", member.Notes));
        }

        await DeleteMissingChildrenAsync(connection, transaction, "PopulationMembers", aquarium.Id, aquarium.Population.Select(member => member.Id), cancellationToken);

        foreach (var intervention in aquarium.Interventions)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO AquariumInterventions (
                    Id, AquariumId, OccurredAt, Type, ProductName, ProductQuantity, WaterVolumeLiters, WaterPercentage,
                    PopulationChangeReason, PopulationChangeCount, Notes)
                VALUES (
                    @Id, @AquariumId, @OccurredAt, @Type, @ProductName, @ProductQuantity, @WaterVolumeLiters, @WaterPercentage,
                    @PopulationChangeReason, @PopulationChangeCount, @Notes)
                ON DUPLICATE KEY UPDATE
                    OccurredAt = VALUES(OccurredAt),
                    Type = VALUES(Type),
                    ProductName = VALUES(ProductName),
                    ProductQuantity = VALUES(ProductQuantity),
                    WaterVolumeLiters = VALUES(WaterVolumeLiters),
                    WaterPercentage = VALUES(WaterPercentage),
                    PopulationChangeReason = VALUES(PopulationChangeReason),
                    PopulationChangeCount = VALUES(PopulationChangeCount),
                    Notes = VALUES(Notes);
                """,
                cancellationToken,
                Parameter("@Id", intervention.Id.ToString()),
                Parameter("@AquariumId", aquarium.Id.ToString()),
                Parameter("@OccurredAt", intervention.OccurredAt),
                Parameter("@Type", intervention.Type.ToString()),
                Parameter("@ProductName", intervention.ProductName),
                Parameter("@ProductQuantity", intervention.ProductQuantity),
                Parameter("@WaterVolumeLiters", intervention.WaterVolumeLiters),
                Parameter("@WaterPercentage", intervention.WaterPercentage),
                Parameter("@PopulationChangeReason", intervention.PopulationChangeReason),
                Parameter("@PopulationChangeCount", intervention.PopulationChangeCount),
                Parameter("@Notes", intervention.Notes));
        }

        await DeleteMissingChildrenAsync(connection, transaction, "AquariumInterventions", aquarium.Id, aquarium.Interventions.Select(intervention => intervention.Id), cancellationToken);
    }

    private static async Task DeleteMissingChildrenAsync(MySqlConnection connection, MySqlTransaction transaction, string tableName, Guid aquariumId, IEnumerable<Guid> retainedIds, CancellationToken cancellationToken)
    {
        if (!ChildTables.Contains(tableName))
        {
            throw new InvalidOperationException($"Unsupported child table '{tableName}'.");
        }

        var ids = retainedIds.Select(id => id.ToString()).ToList();
        if (ids.Count == 0)
        {
            await ExecuteAsync(connection, transaction, $"DELETE FROM {tableName} WHERE AquariumId = @AquariumId;", cancellationToken, Parameter("@AquariumId", aquariumId.ToString()));
            return;
        }

        var parameterNames = ids.Select((_, index) => $"@Id{index}").ToList();
        var parameters = new List<MySqlParameter> { Parameter("@AquariumId", aquariumId.ToString()) };
        parameters.AddRange(ids.Select((id, index) => Parameter(parameterNames[index], id)));

        await ExecuteAsync(
            connection,
            transaction,
            $"DELETE FROM {tableName} WHERE AquariumId = @AquariumId AND Id NOT IN ({string.Join(", ", parameterNames)});",
            cancellationToken,
            parameters.ToArray());
    }

    private static async Task ExecuteAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql, CancellationToken cancellationToken, params MySqlParameter[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MySqlParameter Parameter(string name, object? value)
    {
        return new MySqlParameter
        {
            ParameterName = name,
            Value = value ?? DBNull.Value
        };
    }

    private static DateTime NormalizeDateOrToday(DateTime value)
    {
        return value == default ? DateTime.Today : value.Date;
    }

    private static decimal? ReadNullableDecimal(MySqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    }

    private static Guid ReadGuid(MySqlDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            Guid guid => guid,
            string text => Guid.Parse(text),
            _ => Guid.Parse(Convert.ToString(value) ?? throw new InvalidOperationException($"Column {ordinal} is not a valid Guid."))
        };
    }

    private static async Task EnsurePlantReferencesSeededAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string existsSql = "SELECT COUNT(*) FROM PlantReferences WHERE Environment = @Environment AND ScientificName = @ScientificName;";
        const string insertSql =
            """
            INSERT INTO PlantReferences (Id, Environment, CommonName, CommonNameFr, CommonNameEn, CommonNameDe, ScientificName, PhMin, PhMax, GhMin, GhMax, KhMin, KhMax,
                                         TemperatureMin, TemperatureMax, AmmoniaMin, AmmoniaMax, NitritesMin, NitritesMax,
                                         NitratesMin, NitratesMax, VolumeMinLiters, LightNeed, Co2Need, FertilizationNeed,
                                         GrowthSpeed, RecommendedPlacement, Behavior, Compatibility, SourceUrl)
            VALUES (@Id, @Environment, @CommonName, @CommonNameFr, @CommonNameEn, @CommonNameDe, @ScientificName, @PhMin, @PhMax, @GhMin, @GhMax, @KhMin, @KhMax,
                    @TemperatureMin, @TemperatureMax, @AmmoniaMin, @AmmoniaMax, @NitritesMin, @NitritesMax,
                    @NitratesMin, @NitratesMax, @VolumeMinLiters, @LightNeed, @Co2Need, @FertilizationNeed,
                    @GrowthSpeed, @RecommendedPlacement, @Behavior, @Compatibility, @SourceUrl);
            """;

        foreach (var plant in SeedPlantReferences)
        {
            SanitizePlantReferenceForStorage(plant);
            await using var existsCommand = new MySqlCommand(existsSql, connection);
            existsCommand.Parameters.AddRange(
                new[]
                {
                    Parameter("@Environment", plant.Environment.ToString()),
                    Parameter("@ScientificName", plant.ScientificName)
                });
            var exists = Convert.ToInt32(await existsCommand.ExecuteScalarAsync(cancellationToken)) > 0;
            if (exists)
            {
                continue;
            }

            await using var insertCommand = new MySqlCommand(insertSql, connection);
            insertCommand.Parameters.AddRange(
            new[]
            {
                Parameter("@Id", plant.Id.ToString()),
                Parameter("@Environment", plant.Environment.ToString()),
                Parameter("@CommonName", plant.CommonName),
                Parameter("@CommonNameFr", plant.CommonNameFr),
                Parameter("@CommonNameEn", plant.CommonNameEn),
                Parameter("@CommonNameDe", plant.CommonNameDe),
                Parameter("@ScientificName", plant.ScientificName),
                Parameter("@PhMin", plant.PhMin),
                Parameter("@PhMax", plant.PhMax),
                Parameter("@GhMin", plant.GhMin),
                Parameter("@GhMax", plant.GhMax),
                Parameter("@KhMin", plant.KhMin),
                Parameter("@KhMax", plant.KhMax),
                Parameter("@TemperatureMin", plant.TemperatureMin),
                Parameter("@TemperatureMax", plant.TemperatureMax),
                Parameter("@AmmoniaMin", plant.AmmoniaMin),
                Parameter("@AmmoniaMax", plant.AmmoniaMax),
                Parameter("@NitritesMin", plant.NitritesMin),
                Parameter("@NitritesMax", plant.NitritesMax),
                Parameter("@NitratesMin", plant.NitratesMin),
                Parameter("@NitratesMax", plant.NitratesMax),
                Parameter("@VolumeMinLiters", plant.VolumeMinLiters),
                Parameter("@LightNeed", plant.LightNeed),
                Parameter("@Co2Need", plant.Co2Need),
                Parameter("@FertilizationNeed", plant.FertilizationNeed),
                Parameter("@GrowthSpeed", plant.GrowthSpeed),
                Parameter("@RecommendedPlacement", plant.RecommendedPlacement),
                Parameter("@Behavior", plant.Behavior),
                Parameter("@Compatibility", plant.Compatibility),
                Parameter("@SourceUrl", plant.SourceUrl)
            });
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureAnimalReferencesSeededAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string existsSql = "SELECT COUNT(*) FROM AnimalReferences WHERE Environment = @Environment AND ScientificName = @ScientificName;";
        const string insertSql =
            """
            INSERT INTO AnimalReferences (Id, Environment, CommonName, CommonNameFr, CommonNameEn, CommonNameDe, ScientificName, PhMin, PhMax, GhMin, GhMax, KhMin, KhMax,
                                          TemperatureMin, TemperatureMax, AmmoniaMin, AmmoniaMax, NitritesMin, NitritesMax,
                                          NitratesMin, NitratesMax, VolumeMinLiters, Behavior, Compatibility, SourceUrl)
            VALUES (@Id, @Environment, @CommonName, @CommonNameFr, @CommonNameEn, @CommonNameDe, @ScientificName, @PhMin, @PhMax, @GhMin, @GhMax, @KhMin, @KhMax,
                    @TemperatureMin, @TemperatureMax, @AmmoniaMin, @AmmoniaMax, @NitritesMin, @NitritesMax,
                    @NitratesMin, @NitratesMax, @VolumeMinLiters, @Behavior, @Compatibility, @SourceUrl);
            """;

        foreach (var animal in SeedAnimalReferences)
        {
            SanitizeAnimalReferenceForStorage(animal);
            await using var existsCommand = new MySqlCommand(existsSql, connection);
            existsCommand.Parameters.AddRange(
                new[]
                {
                    Parameter("@Environment", animal.Environment.ToString()),
                    Parameter("@ScientificName", animal.ScientificName)
                });
            var exists = Convert.ToInt32(await existsCommand.ExecuteScalarAsync(cancellationToken)) > 0;
            if (exists)
            {
                continue;
            }

            await using var insertCommand = new MySqlCommand(insertSql, connection);
            insertCommand.Parameters.AddRange(
            new[]
            {
                Parameter("@Id", animal.Id.ToString()),
                Parameter("@Environment", animal.Environment.ToString()),
                Parameter("@CommonName", animal.CommonName),
                Parameter("@CommonNameFr", animal.CommonNameFr),
                Parameter("@CommonNameEn", animal.CommonNameEn),
                Parameter("@CommonNameDe", animal.CommonNameDe),
                Parameter("@ScientificName", animal.ScientificName),
                Parameter("@PhMin", animal.PhMin),
                Parameter("@PhMax", animal.PhMax),
                Parameter("@GhMin", animal.GhMin),
                Parameter("@GhMax", animal.GhMax),
                Parameter("@KhMin", animal.KhMin),
                Parameter("@KhMax", animal.KhMax),
                Parameter("@TemperatureMin", animal.TemperatureMin),
                Parameter("@TemperatureMax", animal.TemperatureMax),
                Parameter("@AmmoniaMin", animal.AmmoniaMin),
                Parameter("@AmmoniaMax", animal.AmmoniaMax),
                Parameter("@NitritesMin", animal.NitritesMin),
                Parameter("@NitritesMax", animal.NitritesMax),
                Parameter("@NitratesMin", animal.NitratesMin),
                Parameter("@NitratesMax", animal.NitratesMax),
                Parameter("@VolumeMinLiters", animal.VolumeMinLiters),
                Parameter("@Behavior", animal.Behavior),
                Parameter("@Compatibility", animal.Compatibility),
                Parameter("@SourceUrl", animal.SourceUrl)
            });
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureAquariumChildSchemaUpgradedAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        var alterStatements = new[]
        {
            "ALTER TABLE AquariumPlants ADD COLUMN AddedOn DATE NULL AFTER AquariumId;",
            "UPDATE AquariumPlants SET AddedOn = CURRENT_DATE WHERE AddedOn IS NULL;",
            "ALTER TABLE AquariumPlants MODIFY AddedOn DATE NOT NULL;",
            "ALTER TABLE PopulationMembers ADD COLUMN AddedOn DATE NULL AFTER AquariumId;",
            "UPDATE PopulationMembers SET AddedOn = CURRENT_DATE WHERE AddedOn IS NULL;",
            "ALTER TABLE PopulationMembers MODIFY AddedOn DATE NOT NULL;"
        };

        foreach (var sql in alterStatements)
        {
            try
            {
                await using var command = new MySqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch
            {
                // Ignore if schema already compatible or statement not supported by provider.
            }
        }
    }

    private static async Task EnsurePlantReferenceSchemaUpgradedAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        var alterStatements = new[]
        {
            "ALTER TABLE PlantReferences ADD COLUMN CommonNameFr VARCHAR(160) NULL AFTER CommonName;",
            "ALTER TABLE PlantReferences ADD COLUMN CommonNameEn VARCHAR(160) NULL AFTER CommonNameFr;",
            "ALTER TABLE PlantReferences ADD COLUMN CommonNameDe VARCHAR(160) NULL AFTER CommonNameEn;",
            "ALTER TABLE PlantReferences MODIFY PhMin DECIMAL(5,2) NULL;",
            "ALTER TABLE PlantReferences MODIFY PhMax DECIMAL(5,2) NULL;",
            "ALTER TABLE PlantReferences MODIFY GhMin DECIMAL(6,2) NULL;",
            "ALTER TABLE PlantReferences MODIFY GhMax DECIMAL(6,2) NULL;",
            "ALTER TABLE PlantReferences MODIFY KhMin DECIMAL(6,2) NULL;",
            "ALTER TABLE PlantReferences MODIFY KhMax DECIMAL(6,2) NULL;",
            "ALTER TABLE PlantReferences MODIFY TemperatureMin DECIMAL(5,2) NULL;",
            "ALTER TABLE PlantReferences MODIFY TemperatureMax DECIMAL(5,2) NULL;",
            "ALTER TABLE PlantReferences MODIFY AmmoniaMin DECIMAL(7,3) NULL;",
            "ALTER TABLE PlantReferences MODIFY AmmoniaMax DECIMAL(7,3) NULL;",
            "ALTER TABLE PlantReferences MODIFY NitritesMin DECIMAL(7,3) NULL;",
            "ALTER TABLE PlantReferences MODIFY NitritesMax DECIMAL(7,3) NULL;",
            "ALTER TABLE PlantReferences MODIFY NitratesMin DECIMAL(7,3) NULL;",
            "ALTER TABLE PlantReferences MODIFY NitratesMax DECIMAL(7,3) NULL;",
            "ALTER TABLE PlantReferences MODIFY VolumeMinLiters INT NULL;"
        };

        foreach (var sql in alterStatements)
        {
            try
            {
                await using var command = new MySqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch
            {
                // Ignore if schema already compatible or statement not supported by provider.
            }
        }
    }

    private static async Task EnsureAnimalReferenceSchemaUpgradedAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        var alterStatements = new[]
        {
            "ALTER TABLE AnimalReferences ADD COLUMN CommonNameFr VARCHAR(160) NULL AFTER CommonName;",
            "ALTER TABLE AnimalReferences ADD COLUMN CommonNameEn VARCHAR(160) NULL AFTER CommonNameFr;",
            "ALTER TABLE AnimalReferences ADD COLUMN CommonNameDe VARCHAR(160) NULL AFTER CommonNameEn;",
            "ALTER TABLE AnimalReferences MODIFY PhMin DECIMAL(5,2) NULL;",
            "ALTER TABLE AnimalReferences MODIFY PhMax DECIMAL(5,2) NULL;",
            "ALTER TABLE AnimalReferences MODIFY GhMin DECIMAL(6,2) NULL;",
            "ALTER TABLE AnimalReferences MODIFY GhMax DECIMAL(6,2) NULL;",
            "ALTER TABLE AnimalReferences MODIFY KhMin DECIMAL(6,2) NULL;",
            "ALTER TABLE AnimalReferences MODIFY KhMax DECIMAL(6,2) NULL;",
            "ALTER TABLE AnimalReferences MODIFY TemperatureMin DECIMAL(5,2) NULL;",
            "ALTER TABLE AnimalReferences MODIFY TemperatureMax DECIMAL(5,2) NULL;",
            "ALTER TABLE AnimalReferences MODIFY AmmoniaMin DECIMAL(7,3) NULL;",
            "ALTER TABLE AnimalReferences MODIFY AmmoniaMax DECIMAL(7,3) NULL;",
            "ALTER TABLE AnimalReferences MODIFY NitritesMin DECIMAL(7,3) NULL;",
            "ALTER TABLE AnimalReferences MODIFY NitritesMax DECIMAL(7,3) NULL;",
            "ALTER TABLE AnimalReferences MODIFY NitratesMin DECIMAL(7,3) NULL;",
            "ALTER TABLE AnimalReferences MODIFY NitratesMax DECIMAL(7,3) NULL;",
            "ALTER TABLE AnimalReferences MODIFY VolumeMinLiters INT NULL;"
        };

        foreach (var sql in alterStatements)
        {
            try
            {
                await using var command = new MySqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch
            {
                // Ignore if schema already compatible or statement not supported by provider.
            }
        }

        await NormalizeInvalidAnimalReferenceRangesAsync(connection, cancellationToken);
    }

    private static async Task NormalizeInvalidAnimalReferenceRangesAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE AnimalReferences target
            JOIN (
                SELECT *
                FROM (
                    SELECT Id,
                           PhMin AS OldPhMin,
                           PhMax AS OldPhMax,
                           GhMin AS OldGhMin,
                           GhMax AS OldGhMax,
                           KhMin AS OldKhMin,
                           KhMax AS OldKhMax,
                           TemperatureMin AS OldTemperatureMin,
                           TemperatureMax AS OldTemperatureMax,
                           AmmoniaMin AS OldAmmoniaMin,
                           AmmoniaMax AS OldAmmoniaMax,
                           NitritesMin AS OldNitritesMin,
                           NitritesMax AS OldNitritesMax,
                           NitratesMin AS OldNitratesMin,
                           NitratesMax AS OldNitratesMax
                    FROM AnimalReferences
                ) snapshot
            ) source ON source.Id = target.Id
            SET target.PhMin = CASE WHEN source.OldPhMin IS NOT NULL AND source.OldPhMax IS NOT NULL AND source.OldPhMax < source.OldPhMin THEN source.OldPhMax ELSE target.PhMin END,
                target.PhMax = CASE WHEN source.OldPhMin IS NOT NULL AND source.OldPhMax IS NOT NULL AND source.OldPhMax < source.OldPhMin THEN source.OldPhMin ELSE target.PhMax END,
                target.GhMin = CASE WHEN source.OldGhMin IS NOT NULL AND source.OldGhMax IS NOT NULL AND source.OldGhMax < source.OldGhMin THEN source.OldGhMax ELSE target.GhMin END,
                target.GhMax = CASE WHEN source.OldGhMin IS NOT NULL AND source.OldGhMax IS NOT NULL AND source.OldGhMax < source.OldGhMin THEN source.OldGhMin ELSE target.GhMax END,
                target.KhMin = CASE WHEN source.OldKhMin IS NOT NULL AND source.OldKhMax IS NOT NULL AND source.OldKhMax < source.OldKhMin THEN source.OldKhMax ELSE target.KhMin END,
                target.KhMax = CASE WHEN source.OldKhMin IS NOT NULL AND source.OldKhMax IS NOT NULL AND source.OldKhMax < source.OldKhMin THEN source.OldKhMin ELSE target.KhMax END,
                target.TemperatureMin = CASE WHEN source.OldTemperatureMin IS NOT NULL AND source.OldTemperatureMax IS NOT NULL AND source.OldTemperatureMax < source.OldTemperatureMin THEN source.OldTemperatureMax ELSE target.TemperatureMin END,
                target.TemperatureMax = CASE WHEN source.OldTemperatureMin IS NOT NULL AND source.OldTemperatureMax IS NOT NULL AND source.OldTemperatureMax < source.OldTemperatureMin THEN source.OldTemperatureMin ELSE target.TemperatureMax END,
                target.AmmoniaMin = CASE WHEN source.OldAmmoniaMin IS NOT NULL AND source.OldAmmoniaMax IS NOT NULL AND source.OldAmmoniaMax < source.OldAmmoniaMin THEN source.OldAmmoniaMax ELSE target.AmmoniaMin END,
                target.AmmoniaMax = CASE WHEN source.OldAmmoniaMin IS NOT NULL AND source.OldAmmoniaMax IS NOT NULL AND source.OldAmmoniaMax < source.OldAmmoniaMin THEN source.OldAmmoniaMin ELSE target.AmmoniaMax END,
                target.NitritesMin = CASE WHEN source.OldNitritesMin IS NOT NULL AND source.OldNitritesMax IS NOT NULL AND source.OldNitritesMax < source.OldNitritesMin THEN source.OldNitritesMax ELSE target.NitritesMin END,
                target.NitritesMax = CASE WHEN source.OldNitritesMin IS NOT NULL AND source.OldNitritesMax IS NOT NULL AND source.OldNitritesMax < source.OldNitritesMin THEN source.OldNitritesMin ELSE target.NitritesMax END,
                target.NitratesMin = CASE WHEN source.OldNitratesMin IS NOT NULL AND source.OldNitratesMax IS NOT NULL AND source.OldNitratesMax < source.OldNitratesMin THEN source.OldNitratesMax ELSE target.NitratesMin END,
                target.NitratesMax = CASE WHEN source.OldNitratesMin IS NOT NULL AND source.OldNitratesMax IS NOT NULL AND source.OldNitratesMax < source.OldNitratesMin THEN source.OldNitratesMin ELSE target.NitratesMax END
            WHERE (target.PhMin IS NOT NULL AND target.PhMax IS NOT NULL AND target.PhMax < target.PhMin)
               OR (target.GhMin IS NOT NULL AND target.GhMax IS NOT NULL AND target.GhMax < target.GhMin)
               OR (target.KhMin IS NOT NULL AND target.KhMax IS NOT NULL AND target.KhMax < target.KhMin)
               OR (target.TemperatureMin IS NOT NULL AND target.TemperatureMax IS NOT NULL AND target.TemperatureMax < target.TemperatureMin)
               OR (target.AmmoniaMin IS NOT NULL AND target.AmmoniaMax IS NOT NULL AND target.AmmoniaMax < target.AmmoniaMin)
               OR (target.NitritesMin IS NOT NULL AND target.NitritesMax IS NOT NULL AND target.NitritesMax < target.NitritesMin)
               OR (target.NitratesMin IS NOT NULL AND target.NitratesMax IS NOT NULL AND target.NitratesMax < target.NitratesMin);
            """;

        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task NormalizeExistingCommonNamesAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        var commonNameColumns = new[]
        {
            ("AquariumPlants", "CommonName"),
            ("PopulationMembers", "CommonName"),
            ("PlantReferences", "CommonName"),
            ("PlantReferences", "CommonNameFr"),
            ("PlantReferences", "CommonNameEn"),
            ("PlantReferences", "CommonNameDe"),
            ("PlantReferenceImportCandidates", "CommonName"),
            ("PlantReferenceImportCandidates", "CommonNameFr"),
            ("PlantReferenceImportCandidates", "CommonNameEn"),
            ("PlantReferenceImportCandidates", "CommonNameDe"),
            ("AnimalReferences", "CommonName"),
            ("AnimalReferences", "CommonNameFr"),
            ("AnimalReferences", "CommonNameEn"),
            ("AnimalReferences", "CommonNameDe"),
            ("AnimalReferenceImportCandidates", "CommonName"),
            ("AnimalReferenceImportCandidates", "CommonNameFr"),
            ("AnimalReferenceImportCandidates", "CommonNameEn"),
            ("AnimalReferenceImportCandidates", "CommonNameDe")
        };

        foreach (var (tableName, columnName) in commonNameColumns)
        {
            await NormalizeExistingCommonNameColumnAsync(connection, tableName, columnName, cancellationToken);
        }
    }

    private static async Task NormalizeExistingCommonNameColumnAsync(
        MySqlConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        var sql =
            $"""
            UPDATE `{tableName}`
            SET `{columnName}` = CONCAT(UPPER(LEFT(TRIM(`{columnName}`), 1)), SUBSTRING(TRIM(`{columnName}`), 2))
            WHERE `{columnName}` IS NOT NULL
              AND TRIM(`{columnName}`) <> '';
            """;

        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsurePlantReferenceImportCandidateSchemaAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string createSql = """
            CREATE TABLE IF NOT EXISTS PlantReferenceImportCandidates (
                Id CHAR(36) NOT NULL PRIMARY KEY,
                RunId CHAR(36) NOT NULL,
                CollectedAt DATETIME NOT NULL,
                SourceName VARCHAR(80) NOT NULL,
                SourceUrl VARCHAR(512) NOT NULL,
                Environment VARCHAR(40) NOT NULL,
                CommonName VARCHAR(160) NULL,
                CommonNameFr VARCHAR(160) NULL,
                CommonNameEn VARCHAR(160) NULL,
                CommonNameDe VARCHAR(160) NULL,
                ScientificName VARCHAR(180) NULL,
                PhMin DECIMAL(5,2) NULL,
                PhMax DECIMAL(5,2) NULL,
                GhMin DECIMAL(6,2) NULL,
                GhMax DECIMAL(6,2) NULL,
                KhMin DECIMAL(6,2) NULL,
                KhMax DECIMAL(6,2) NULL,
                TemperatureMin DECIMAL(5,2) NULL,
                TemperatureMax DECIMAL(5,2) NULL,
                AmmoniaMin DECIMAL(7,3) NULL,
                AmmoniaMax DECIMAL(7,3) NULL,
                NitritesMin DECIMAL(7,3) NULL,
                NitritesMax DECIMAL(7,3) NULL,
                NitratesMin DECIMAL(7,3) NULL,
                NitratesMax DECIMAL(7,3) NULL,
                VolumeMinLiters INT NULL,
                LightNeed VARCHAR(120) NULL,
                Co2Need VARCHAR(120) NULL,
                FertilizationNeed VARCHAR(120) NULL,
                GrowthSpeed VARCHAR(80) NULL,
                RecommendedPlacement VARCHAR(120) NULL,
                Behavior VARCHAR(180) NULL,
                Compatibility VARCHAR(220) NULL,
                EssentialParameterCount INT NOT NULL,
                CandidateStatus VARCHAR(60) NOT NULL,
                RejectionReason VARCHAR(220) NOT NULL,
                INDEX IX_PlantReferenceImportCandidates_RunId (RunId),
                INDEX IX_PlantReferenceImportCandidates_SourceName (SourceName),
                INDEX IX_PlantReferenceImportCandidates_ScientificName (ScientificName),
                INDEX IX_PlantReferenceImportCandidates_Status (CandidateStatus)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """;

        await using var command = new MySqlCommand(createSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        var alterStatements = new[]
        {
            "ALTER TABLE PlantReferenceImportCandidates ADD COLUMN CommonNameFr VARCHAR(160) NULL AFTER CommonName;",
            "ALTER TABLE PlantReferenceImportCandidates ADD COLUMN CommonNameEn VARCHAR(160) NULL AFTER CommonNameFr;",
            "ALTER TABLE PlantReferenceImportCandidates ADD COLUMN CommonNameDe VARCHAR(160) NULL AFTER CommonNameEn;"
        };

        foreach (var sql in alterStatements)
        {
            try
            {
                await using var alter = new MySqlCommand(sql, connection);
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
            catch
            {
                // Ignore if schema already compatible.
            }
        }
    }

    private static async Task EnsureAnimalReferenceImportCandidateSchemaAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string createSql = """
            CREATE TABLE IF NOT EXISTS AnimalReferenceImportCandidates (
                Id CHAR(36) NOT NULL PRIMARY KEY,
                RunId CHAR(36) NOT NULL,
                CollectedAt DATETIME NOT NULL,
                SourceName VARCHAR(80) NOT NULL,
                SourceUrl VARCHAR(512) NOT NULL,
                Environment VARCHAR(40) NOT NULL,
                CommonName VARCHAR(160) NULL,
                CommonNameFr VARCHAR(160) NULL,
                CommonNameEn VARCHAR(160) NULL,
                CommonNameDe VARCHAR(160) NULL,
                ScientificName VARCHAR(180) NULL,
                PhMin DECIMAL(5,2) NULL,
                PhMax DECIMAL(5,2) NULL,
                GhMin DECIMAL(6,2) NULL,
                GhMax DECIMAL(6,2) NULL,
                KhMin DECIMAL(6,2) NULL,
                KhMax DECIMAL(6,2) NULL,
                TemperatureMin DECIMAL(5,2) NULL,
                TemperatureMax DECIMAL(5,2) NULL,
                AmmoniaMin DECIMAL(7,3) NULL,
                AmmoniaMax DECIMAL(7,3) NULL,
                NitritesMin DECIMAL(7,3) NULL,
                NitritesMax DECIMAL(7,3) NULL,
                NitratesMin DECIMAL(7,3) NULL,
                NitratesMax DECIMAL(7,3) NULL,
                VolumeMinLiters INT NULL,
                EssentialParameterCount INT NOT NULL,
                CandidateStatus VARCHAR(60) NOT NULL,
                RejectionReason VARCHAR(220) NOT NULL,
                INDEX IX_AnimalReferenceImportCandidates_RunId (RunId),
                INDEX IX_AnimalReferenceImportCandidates_SourceName (SourceName),
                INDEX IX_AnimalReferenceImportCandidates_ScientificName (ScientificName),
                INDEX IX_AnimalReferenceImportCandidates_Status (CandidateStatus)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """;

        await using var command = new MySqlCommand(createSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        var alterStatements = new[]
        {
            "ALTER TABLE AnimalReferenceImportCandidates ADD COLUMN CommonNameFr VARCHAR(160) NULL AFTER CommonName;",
            "ALTER TABLE AnimalReferenceImportCandidates ADD COLUMN CommonNameEn VARCHAR(160) NULL AFTER CommonNameFr;",
            "ALTER TABLE AnimalReferenceImportCandidates ADD COLUMN CommonNameDe VARCHAR(160) NULL AFTER CommonNameEn;"
        };

        foreach (var sql in alterStatements)
        {
            try
            {
                await using var alter = new MySqlCommand(sql, connection);
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
            catch
            {
                // Ignore if schema already compatible.
            }
        }
    }

    public async Task<int> ImportPlantReferencesFromWebAsync(
        IProgress<string>? progress = null,
        int minimumEssentialParameterGroups = MinimumPlantEssentialParameterCount,
        CancellationToken cancellationToken = default)
    {
        var minimumParameterGroupCount = NormalizeMinimumReferenceParameterGroups(minimumEssentialParameterGroups);
        progress?.Report("Plantes: initialisation de l'import...");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsurePlantReferenceSchemaUpgradedAsync(connection, cancellationToken);
        await EnsurePlantReferenceImportCandidateSchemaAsync(connection, cancellationToken);
        await ClearPlantReferenceImportCandidatesAsync(connection, cancellationToken);
        await EnsurePlantReferencesSeededAsync(connection, cancellationToken);
        await CleanupInvalidAutoPlantReferencesAsync(connection, cancellationToken);

        progress?.Report("Plantes: collecte multi-sources et consolidation des parametres...");
        var counters = new PlantImportCounters { RunId = Guid.NewGuid() };
        var discovered = await DiscoverPlantReferencesFromWebAsync(counters, minimumParameterGroupCount, progress, cancellationToken);
        using var http = CreateWikiHttpClient();

        progress?.Report($"Plantes: recherche des noms communs FR/EN/DE pour {discovered.Count} fiches candidates...");
        for (var index = 0; index < discovered.Count; index++)
        {
            await EnrichPlantCommonNamesFromWikipediaAsync(http, discovered[index], cancellationToken);
            SanitizePlantReferenceForStorage(discovered[index]);
            if ((index + 1) % 25 == 0 || index + 1 == discovered.Count)
            {
                progress?.Report($"Plantes: noms communs FR/EN/DE {index + 1}/{discovered.Count}...");
            }
        }

        ApplyPlantCommonNamesToImportCandidates(counters, discovered);
        await PersistPlantReferenceImportCandidatesAsync(connection, counters, progress, cancellationToken);

        var candidates = discovered
            .Where(reference => CountEssentialPlantParameters(reference) >= minimumParameterGroupCount)
            .OrderBy(reference => reference.Environment)
            .ThenBy(reference => reference.ScientificName)
            .ToList();

        counters.Incomplete += discovered.Count - candidates.Count;
        counters.Skipped += counters.Incomplete;
        progress?.Report($"Plantes: consolidation terminee ({counters.Candidates.Count} fiches candidates journalisees, {discovered.Count} plantes candidates, {candidates.Count} exploitables avec au moins {minimumParameterGroupCount} groupes de parametres, {counters.Incomplete} incompletes).");

        const string existsSql = "SELECT COUNT(*) FROM PlantReferences WHERE ScientificName = @ScientificName;";
        const string insertSql =
            """
            INSERT INTO PlantReferences (Id, Environment, CommonName, CommonNameFr, CommonNameEn, CommonNameDe, ScientificName, PhMin, PhMax, GhMin, GhMax, KhMin, KhMax,
                                         TemperatureMin, TemperatureMax, AmmoniaMin, AmmoniaMax, NitritesMin, NitritesMax,
                                         NitratesMin, NitratesMax, VolumeMinLiters, LightNeed, Co2Need, FertilizationNeed,
                                         GrowthSpeed, RecommendedPlacement, Behavior, Compatibility, SourceUrl)
            VALUES (@Id, @Environment, @CommonName, @CommonNameFr, @CommonNameEn, @CommonNameDe, @ScientificName, @PhMin, @PhMax, @GhMin, @GhMax, @KhMin, @KhMax,
                    @TemperatureMin, @TemperatureMax, @AmmoniaMin, @AmmoniaMax, @NitritesMin, @NitritesMax,
                    @NitratesMin, @NitratesMax, @VolumeMinLiters, @LightNeed, @Co2Need, @FertilizationNeed,
                    @GrowthSpeed, @RecommendedPlacement, @Behavior, @Compatibility, @SourceUrl);
            """;

        const int batchSize = 100;
        for (var offset = 0; offset < candidates.Count; offset += batchSize)
        {
            counters.BatchIndex++;
            var batch = candidates.Skip(offset).Take(batchSize).ToList();
            progress?.Report($"Plantes: insertion du paquet {counters.BatchIndex} ({batch.Count} plantes)...");
            await InsertPlantReferenceBatchAsync(connection, existsSql, insertSql, batch, counters, minimumParameterGroupCount, progress, cancellationToken);
            progress?.Report($"Plantes: paquet {counters.BatchIndex} termine (ajoutees: {counters.Inserted}, deja presentes: {counters.Existing}, incompletes: {counters.Incomplete}, erreurs: {counters.Failed}).");
        }

        progress?.Report($"Plantes: import termine ({counters.Inserted} nouvelles plantes, {counters.Existing} deja presentes, {counters.Incomplete} incompletes, erreurs: {counters.Failed}).");
        return counters.Inserted;
    }

    public async Task UpdatePlantReferenceAsync(PlantReference reference, CancellationToken cancellationToken = default)
    {
        SanitizePlantReferenceForStorage(reference);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsurePlantReferenceSchemaUpgradedAsync(connection, cancellationToken);

        const string sql =
            """
            UPDATE PlantReferences
            SET Environment = @Environment,
                CommonName = @CommonName,
                CommonNameFr = @CommonNameFr,
                CommonNameEn = @CommonNameEn,
                CommonNameDe = @CommonNameDe,
                ScientificName = @ScientificName,
                PhMin = @PhMin,
                PhMax = @PhMax,
                GhMin = @GhMin,
                GhMax = @GhMax,
                KhMin = @KhMin,
                KhMax = @KhMax,
                TemperatureMin = @TemperatureMin,
                TemperatureMax = @TemperatureMax,
                AmmoniaMin = @AmmoniaMin,
                AmmoniaMax = @AmmoniaMax,
                NitritesMin = @NitritesMin,
                NitritesMax = @NitritesMax,
                NitratesMin = @NitratesMin,
                NitratesMax = @NitratesMax,
                VolumeMinLiters = @VolumeMinLiters,
                LightNeed = @LightNeed,
                Co2Need = @Co2Need,
                FertilizationNeed = @FertilizationNeed,
                GrowthSpeed = @GrowthSpeed,
                RecommendedPlacement = @RecommendedPlacement,
                Behavior = @Behavior,
                Compatibility = @Compatibility,
                SourceUrl = @SourceUrl
            WHERE Id = @Id;
            """;

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddRange(new[]
        {
            Parameter("@Id", reference.Id.ToString()),
            Parameter("@Environment", reference.Environment.ToString()),
            Parameter("@CommonName", reference.CommonName),
            Parameter("@CommonNameFr", reference.CommonNameFr),
            Parameter("@CommonNameEn", reference.CommonNameEn),
            Parameter("@CommonNameDe", reference.CommonNameDe),
            Parameter("@ScientificName", reference.ScientificName),
            Parameter("@PhMin", reference.PhMin),
            Parameter("@PhMax", reference.PhMax),
            Parameter("@GhMin", reference.GhMin),
            Parameter("@GhMax", reference.GhMax),
            Parameter("@KhMin", reference.KhMin),
            Parameter("@KhMax", reference.KhMax),
            Parameter("@TemperatureMin", reference.TemperatureMin),
            Parameter("@TemperatureMax", reference.TemperatureMax),
            Parameter("@AmmoniaMin", reference.AmmoniaMin),
            Parameter("@AmmoniaMax", reference.AmmoniaMax),
            Parameter("@NitritesMin", reference.NitritesMin),
            Parameter("@NitritesMax", reference.NitritesMax),
            Parameter("@NitratesMin", reference.NitratesMin),
            Parameter("@NitratesMax", reference.NitratesMax),
            Parameter("@VolumeMinLiters", reference.VolumeMinLiters),
            Parameter("@LightNeed", reference.LightNeed),
            Parameter("@Co2Need", reference.Co2Need),
            Parameter("@FertilizationNeed", reference.FertilizationNeed),
            Parameter("@GrowthSpeed", reference.GrowthSpeed),
            Parameter("@RecommendedPlacement", reference.RecommendedPlacement),
            Parameter("@Behavior", reference.Behavior),
            Parameter("@Compatibility", reference.Compatibility),
            Parameter("@SourceUrl", reference.SourceUrl)
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeletePlantReferenceAsync(Guid plantReferenceId, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("DELETE FROM PlantReferences WHERE Id = @Id;", connection);
        command.Parameters.AddWithValue("@Id", plantReferenceId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ResetPlantReferencesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsurePlantReferenceSchemaUpgradedAsync(connection, cancellationToken);
        await using (var deleteCommand = new MySqlCommand("DELETE FROM PlantReferences;", connection))
        {
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsurePlantReferencesSeededAsync(connection, cancellationToken);
    }

    public async Task<int> ImportAnimalReferencesFromWebAsync(
        IProgress<string>? progress = null,
        int minimumEssentialParameterGroups = MinimumAnimalEssentialParameterCount,
        CancellationToken cancellationToken = default)
    {
        var minimumParameterGroupCount = NormalizeMinimumAnimalEssentialParameterGroups(minimumEssentialParameterGroups);
        progress?.Report("Population: initialisation de l'import...");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureAnimalReferenceSchemaUpgradedAsync(connection, cancellationToken);
        await EnsureAnimalReferenceImportCandidateSchemaAsync(connection, cancellationToken);
        await ClearAnimalReferenceImportCandidatesAsync(connection, cancellationToken);
        await EnsureAnimalReferencesSeededAsync(connection, cancellationToken);
        await CleanupInvalidAutoAnimalReferencesAsync(connection, cancellationToken);
        const string existsSql = "SELECT COUNT(*) FROM AnimalReferences WHERE ScientificName = @ScientificName;";
        const string insertSql =
            """
            INSERT INTO AnimalReferences (Id, Environment, CommonName, CommonNameFr, CommonNameEn, CommonNameDe, ScientificName, PhMin, PhMax, GhMin, GhMax, KhMin, KhMax,
                                          TemperatureMin, TemperatureMax, AmmoniaMin, AmmoniaMax, NitritesMin, NitritesMax,
                                          NitratesMin, NitratesMax, VolumeMinLiters, Behavior, Compatibility, SourceUrl)
            VALUES (@Id, @Environment, @CommonName, @CommonNameFr, @CommonNameEn, @CommonNameDe, @ScientificName, @PhMin, @PhMax, @GhMin, @GhMax, @KhMin, @KhMax,
                    @TemperatureMin, @TemperatureMax, @AmmoniaMin, @AmmoniaMax, @NitritesMin, @NitritesMax,
                    @NitratesMin, @NitratesMax, @VolumeMinLiters, @Behavior, @Compatibility, @SourceUrl);
            """;
        progress?.Report("Population: collecte multi-sources et consolidation des parametres...");
        var counters = new AnimalImportCounters { RunId = Guid.NewGuid() };
        var aggregates = new Dictionary<string, AnimalReferenceAggregate>(StringComparer.OrdinalIgnoreCase);
        using var http = CreateWikiHttpClient();
        await CollectFreshwaterWikipediaListAsync(http, aggregates, counters, minimumParameterGroupCount, progress, cancellationToken);
        await CollectFishFishReferencesAsync(http, aggregates, counters, minimumParameterGroupCount, progress, cancellationToken);
        await CollectFishipediaReferencesAsync(http, aggregates, counters, minimumParameterGroupCount, progress, cancellationToken);
        await CollectLiveAquariaReferencesAsync(http, aggregates, counters, minimumParameterGroupCount, progress, cancellationToken);
        await CollectWikipediaCategoryReferencesAsync(http, aggregates, "Freshwater_aquarium_fish", AnimalReferenceEnvironment.FreshwaterTropical, counters, minimumParameterGroupCount, progress, cancellationToken);
        await CollectWikipediaCategoryReferencesAsync(http, aggregates, "Marine_aquarium_fish", AnimalReferenceEnvironment.Marine, counters, minimumParameterGroupCount, progress, cancellationToken);

        var allConsolidated = aggregates.Values
            .Select(aggregate => aggregate.ToReference())
            .Select(reference =>
            {
                SanitizeAnimalReferenceForStorage(reference);
                return reference;
            })
            .OrderBy(reference => reference.Environment)
            .ThenBy(reference => reference.ScientificName)
            .ToList();

        progress?.Report($"Population: recherche des noms communs FR/EN/DE pour {allConsolidated.Count} especes candidates...");
        for (var index = 0; index < allConsolidated.Count; index++)
        {
            await EnrichAnimalCommonNamesFromWikipediaAsync(http, allConsolidated[index], cancellationToken);
            SanitizeAnimalReferenceForStorage(allConsolidated[index]);
            if ((index + 1) % 25 == 0 || index + 1 == allConsolidated.Count)
            {
                progress?.Report($"Population: noms communs FR/EN/DE {index + 1}/{allConsolidated.Count}...");
            }
        }

        ApplyAnimalCommonNamesToImportCandidates(counters, allConsolidated);
        await PersistAnimalReferenceImportCandidatesAsync(connection, counters, progress, cancellationToken);

        var consolidated = allConsolidated
            .Where(reference => CountEssentialAnimalParameters(reference) >= minimumParameterGroupCount)
            .ToList();

        counters.Incomplete += aggregates.Count - consolidated.Count;
        counters.Skipped += counters.Incomplete;
        progress?.Report($"Population: consolidation terminee ({counters.Candidates.Count} fiches candidates journalisees, {aggregates.Count} especes candidates, {consolidated.Count} exploitables avec au moins {minimumParameterGroupCount} groupes de parametres, {counters.Incomplete} incompletes).");

        for (var index = 0; index < consolidated.Count; index += 100)
        {
            var batch = consolidated.Skip(index).Take(100).ToList();
            progress?.Report($"Population: insertion paquet {counters.BatchIndex} ({batch.Count} especes consolidees)...");
            await InsertAnimalReferenceBatchAsync(connection, existsSql, insertSql, batch, counters, minimumParameterGroupCount, progress, cancellationToken);
            progress?.Report($"Population: paquet {counters.BatchIndex} termine (ajoutees: {counters.Inserted}, deja presentes: {counters.Existing}, enrichies: {counters.ExistingUpdated}, incompletes: {counters.Incomplete}, erreurs: {counters.Failed}).");
            counters.BatchIndex++;
        }

        progress?.Report($"Population: import termine ({counters.Inserted} nouvelles especes, {counters.Existing} deja presentes, {counters.ExistingUpdated} enrichies, {counters.Incomplete} incompletes, erreurs: {counters.Failed}).");
        return counters.Inserted;
    }

    public async Task DeleteAnimalReferenceAsync(Guid animalReferenceId, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("DELETE FROM AnimalReferences WHERE Id = @Id;", connection);
        command.Parameters.AddWithValue("@Id", animalReferenceId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAnimalReferenceAsync(AnimalReference reference, CancellationToken cancellationToken = default)
    {
        SanitizeAnimalReferenceForStorage(reference);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureAnimalReferenceSchemaUpgradedAsync(connection, cancellationToken);
        await using var command = new MySqlCommand(
            """
            UPDATE AnimalReferences
            SET Environment = @Environment,
                CommonName = @CommonName,
                CommonNameFr = @CommonNameFr,
                CommonNameEn = @CommonNameEn,
                CommonNameDe = @CommonNameDe,
                ScientificName = @ScientificName,
                PhMin = @PhMin,
                PhMax = @PhMax,
                GhMin = @GhMin,
                GhMax = @GhMax,
                KhMin = @KhMin,
                KhMax = @KhMax,
                TemperatureMin = @TemperatureMin,
                TemperatureMax = @TemperatureMax,
                AmmoniaMin = @AmmoniaMin,
                AmmoniaMax = @AmmoniaMax,
                NitritesMin = @NitritesMin,
                NitritesMax = @NitritesMax,
                NitratesMin = @NitratesMin,
                NitratesMax = @NitratesMax,
                VolumeMinLiters = @VolumeMinLiters,
                Behavior = @Behavior,
                Compatibility = @Compatibility,
                SourceUrl = @SourceUrl
            WHERE Id = @Id;
            """, connection);
        command.Parameters.AddRange(new[]
        {
            Parameter("@Id", reference.Id.ToString()),
            Parameter("@Environment", reference.Environment.ToString()),
            Parameter("@CommonName", reference.CommonName),
            Parameter("@CommonNameFr", reference.CommonNameFr),
            Parameter("@CommonNameEn", reference.CommonNameEn),
            Parameter("@CommonNameDe", reference.CommonNameDe),
            Parameter("@ScientificName", reference.ScientificName),
            Parameter("@PhMin", reference.PhMin),
            Parameter("@PhMax", reference.PhMax),
            Parameter("@GhMin", reference.GhMin),
            Parameter("@GhMax", reference.GhMax),
            Parameter("@KhMin", reference.KhMin),
            Parameter("@KhMax", reference.KhMax),
            Parameter("@TemperatureMin", reference.TemperatureMin),
            Parameter("@TemperatureMax", reference.TemperatureMax),
            Parameter("@AmmoniaMin", reference.AmmoniaMin),
            Parameter("@AmmoniaMax", reference.AmmoniaMax),
            Parameter("@NitritesMin", reference.NitritesMin),
            Parameter("@NitritesMax", reference.NitritesMax),
            Parameter("@NitratesMin", reference.NitratesMin),
            Parameter("@NitratesMax", reference.NitratesMax),
            Parameter("@VolumeMinLiters", reference.VolumeMinLiters),
            Parameter("@Behavior", reference.Behavior),
            Parameter("@Compatibility", reference.Compatibility),
            Parameter("@SourceUrl", reference.SourceUrl)
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ResetAnimalReferencesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var deleteCommand = new MySqlCommand("DELETE FROM AnimalReferences;", connection))
        {
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureAnimalReferenceSchemaUpgradedAsync(connection, cancellationToken);
        await EnsureAnimalReferencesSeededAsync(connection, cancellationToken);
    }

    public async Task<int> GetPlantReferenceCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("SELECT COUNT(*) FROM PlantReferences;", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<int> GetAnimalReferenceCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("SELECT COUNT(*) FROM AnimalReferences;", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<IReadOnlyList<PlantReference>> DiscoverPlantReferencesFromWebAsync(
        PlantImportCounters counters,
        int minimumParameterGroupCount,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var sources = new[]
        {
            (Name: "Fishipedia plantes", Url: "https://www.fishipedia.fr/fr/plants", Environment: PlantReferenceEnvironment.FreshwaterTropical),
            (Name: "Tropica plantes", Url: "https://tropica.com/en/plants/search", Environment: PlantReferenceEnvironment.FreshwaterTropical),
            (Name: "Aquaplante", Url: "https://www.aquaplante.fr/", Environment: PlantReferenceEnvironment.FreshwaterTropical)
        };
        var regex = new Regex(@"\b([A-Z][a-z]{2,})\s([a-z][a-z\-]{2,})\b", RegexOptions.Compiled);
        var found = new Dictionary<string, PlantReference>(StringComparer.OrdinalIgnoreCase);
        using var http = CreateWikiHttpClient();

        foreach (var source in sources)
        {
            progress?.Report($"Plantes: collecte {source.Name}...");
            var html = await TryGetStringWithRetryAsync(http, source.Url, cancellationToken);
            if (html is null)
            {
                RecordPlantImportCandidate(counters, source.Name, source.Url, source.Environment, null, "FetchFailed", "Page source inaccessible.");
                continue;
            }

            foreach (Match match in regex.Matches(html))
            {
                var genus = match.Groups[1].Value;
                var species = match.Groups[2].Value;
                if (!LooksLikeScientificName(genus, species))
                {
                    continue;
                }
                if (!HasScientificContext(html, match.Index, match.Length))
                {
                    continue;
                }

                var scientific = NormalizeScientificName(genus, species);
                if (found.ContainsKey(scientific))
                {
                    continue;
                }

                var detailUrl = BuildPlantDetailUrl(source.Url, scientific);
                var reference = new PlantReference
                {
                    Environment = source.Environment,
                    CommonName = scientific,
                    ScientificName = scientific,
                    SourceUrl = detailUrl
                };
                if (string.Equals(detailUrl, source.Url, StringComparison.OrdinalIgnoreCase))
                {
                    PopulatePlantParametersFromText(reference, CleanHtmlText(html));
                    AssignLocalizedPlantCommonNameFromSource(reference, source.Url);
                }
                else
                {
                    await TryEnrichPlantReferenceFromSourcePageAsync(http, reference, cancellationToken);
                }
                SanitizePlantReferenceForStorage(reference);
                found[scientific] = reference;
                counters.Discovered++;

                var score = CountEssentialPlantParameters(reference);
                RecordPlantImportCandidate(
                    counters,
                    source.Name,
                    reference.SourceUrl,
                    source.Environment,
                    reference,
                    score >= minimumParameterGroupCount ? "Candidate" : "CandidateIncomplete",
                    score >= minimumParameterGroupCount ? string.Empty : BuildPlantCandidateRejectionReason(reference, minimumParameterGroupCount));
            }

            progress?.Report($"Plantes: {source.Name} analyse ({found.Count} plantes candidates cumulees).");
        }

        return found.Values.ToList();
    }

    private static string BuildPlantDetailUrl(string sourceUrl, string scientificName)
    {
        if (sourceUrl.Contains("fishipedia.fr", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://www.fishipedia.fr/fr/plants/{scientificName.ToLowerInvariant().Replace(' ', '-')}";
        }

        return sourceUrl;
    }

    private static async Task TryEnrichPlantReferenceFromSourcePageAsync(
        HttpClient http,
        PlantReference reference,
        CancellationToken cancellationToken)
    {
        var html = await TryGetStringWithRetryAsync(http, reference.SourceUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return;
        }

        var text = CleanHtmlText(html);
        PopulatePlantParametersFromText(reference, text);
        AssignLocalizedPlantCommonNameFromSource(reference, reference.SourceUrl);
    }

    private static async Task<IReadOnlyList<AnimalReference>> DiscoverAnimalReferencesFromWebAsync(CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, AnimalReference>(StringComparer.OrdinalIgnoreCase);

        await AddWikipediaCategorySpeciesAsync(
            found,
            "https://en.wikipedia.org/w/api.php?action=query&list=categorymembers&cmtitle=Category:Freshwater_aquarium_fish&cmlimit=max&format=json",
            AnimalReferenceEnvironment.FreshwaterTropical,
            "https://en.wikipedia.org/wiki/",
            cancellationToken);
        await AddWikipediaCategorySpeciesAsync(
            found,
            "https://en.wikipedia.org/w/api.php?action=query&list=categorymembers&cmtitle=Category:Marine_aquarium_fish&cmlimit=max&format=json",
            AnimalReferenceEnvironment.Marine,
            "https://en.wikipedia.org/wiki/",
            cancellationToken);

        await AddWikipediaListSpeciesAsync(
            found,
            "https://en.wikipedia.org/w/api.php?action=parse&page=List_of_freshwater_aquarium_fish_species&prop=wikitext&format=json",
            AnimalReferenceEnvironment.FreshwaterTropical,
            cancellationToken);
        await AddWikipediaListSpeciesAsync(
            found,
            "https://en.wikipedia.org/w/api.php?action=parse&page=List_of_marine_aquarium_fish_species&prop=wikitext&format=json",
            AnimalReferenceEnvironment.Marine,
            cancellationToken);

        return found.Values.ToList();
    }

    private static async Task AddWikipediaCategorySpeciesAsync(
        Dictionary<string, AnimalReference> found,
        string apiUrl,
        AnimalReferenceEnvironment environment,
        string wikiBaseUrl,
        CancellationToken cancellationToken)
    {
        using var http = CreateWikiHttpClient();
        var json = await TryGetStringWithRetryAsync(http, apiUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("query", out var queryElement)
                || !queryElement.TryGetProperty("categorymembers", out var members)
                || members.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var member in members.EnumerateArray())
            {
                if (!member.TryGetProperty("title", out var titleElement))
                {
                    continue;
                }

                var title = titleElement.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(title) || title.Contains(':'))
                {
                    continue;
                }

                var parts = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                var genus = parts[0];
                var species = parts[1];
                if (!LooksLikeScientificName(genus, species))
                {
                    continue;
                }
                if (parts.Length > 2 && !IsSubspeciesToken(parts[2]))
                {
                    continue;
                }

                var scientific = NormalizeScientificName(genus, species);
                if (found.ContainsKey(scientific))
                {
                    continue;
                }

                var reference = new AnimalReference
                {
                    Environment = environment,
                    CommonName = scientific,
                    ScientificName = scientific,
                    SourceUrl = $"{wikiBaseUrl}{title.Replace(' ', '_')}"
                };
                await EnrichAnimalReferenceFromWikipediaAsync(http, title, reference, cancellationToken);
                found[scientific] = reference;
            }
        }
        catch
        {
            // Ignore malformed JSON payloads from external source.
        }
    }

    private static async Task AddWikipediaListSpeciesAsync(
        Dictionary<string, AnimalReference> found,
        string apiUrl,
        AnimalReferenceEnvironment environment,
        CancellationToken cancellationToken)
    {
        using var http = CreateWikiHttpClient();
        var json = await TryGetStringWithRetryAsync(http, apiUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("parse", out var parseElement)
                || !parseElement.TryGetProperty("wikitext", out var wikiTextElement)
                || !wikiTextElement.TryGetProperty("*", out var textElement))
            {
                return;
            }

            var wikiText = textElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(wikiText))
            {
                return;
            }

            var regex = new Regex(@"\b([A-Z][a-z]{2,})\s([a-z][a-z\-]{2,})\b", RegexOptions.Compiled);
            foreach (Match match in regex.Matches(wikiText))
            {
                var genus = match.Groups[1].Value;
                var species = match.Groups[2].Value;
                if (!LooksLikeScientificName(genus, species))
                {
                    continue;
                }

                var scientific = NormalizeScientificName(genus, species);
                if (found.ContainsKey(scientific))
                {
                    continue;
                }

                var reference = new AnimalReference
                {
                    Environment = environment,
                    CommonName = scientific,
                    ScientificName = scientific,
                    SourceUrl = $"https://en.wikipedia.org/wiki/{scientific.Replace(' ', '_')}"
                };
                await EnrichAnimalReferenceFromWikipediaAsync(http, scientific.Replace(' ', '_'), reference, cancellationToken);
                found[scientific] = reference;
            }
        }
        catch
        {
            // Ignore malformed JSON payloads from external source.
        }
    }

    private static HttpClient CreateWikiHttpClient()
    {
        var client = new HttpClient { Timeout = WebRequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ADAqua/1.0 (+https://localhost)");
        return client;
    }

    private sealed class PlantImportCounters
    {
        public Guid RunId { get; set; }
        public int Inserted { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public int Existing { get; set; }
        public int Incomplete { get; set; }
        public int Processed { get; set; }
        public int Discovered { get; set; }
        public int BatchIndex { get; set; }
        public List<PlantReferenceImportCandidate> Candidates { get; } = [];
    }

    private sealed class PlantReferenceImportCandidate
    {
        public Guid RunId { get; init; }
        public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
        public string SourceName { get; init; } = string.Empty;
        public string SourceUrl { get; init; } = string.Empty;
        public PlantReferenceEnvironment Environment { get; init; } = PlantReferenceEnvironment.FreshwaterTropical;
        public string? CommonName { get; set; }
        public string? CommonNameFr { get; set; }
        public string? CommonNameEn { get; set; }
        public string? CommonNameDe { get; set; }
        public string? ScientificName { get; init; }
        public decimal? PhMin { get; init; }
        public decimal? PhMax { get; init; }
        public decimal? GhMin { get; init; }
        public decimal? GhMax { get; init; }
        public decimal? KhMin { get; init; }
        public decimal? KhMax { get; init; }
        public decimal? TemperatureMin { get; init; }
        public decimal? TemperatureMax { get; init; }
        public decimal? AmmoniaMin { get; init; }
        public decimal? AmmoniaMax { get; init; }
        public decimal? NitritesMin { get; init; }
        public decimal? NitritesMax { get; init; }
        public decimal? NitratesMin { get; init; }
        public decimal? NitratesMax { get; init; }
        public int? VolumeMinLiters { get; init; }
        public string? LightNeed { get; init; }
        public string? Co2Need { get; init; }
        public string? FertilizationNeed { get; init; }
        public string? GrowthSpeed { get; init; }
        public string? RecommendedPlacement { get; init; }
        public string? Behavior { get; init; }
        public string? Compatibility { get; init; }
        public int EssentialParameterCount { get; init; }
        public string CandidateStatus { get; init; } = string.Empty;
        public string RejectionReason { get; init; } = string.Empty;
    }

    private sealed class AnimalImportCounters
    {
        public Guid RunId { get; set; }
        public int Inserted { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public int Existing { get; set; }
        public int ExistingUpdated { get; set; }
        public int Incomplete { get; set; }
        public int Processed { get; set; }
        public int Discovered { get; set; }
        public int BatchIndex { get; set; } = 1;
        public List<AnimalReferenceImportCandidate> Candidates { get; } = [];
    }

    private sealed class AnimalReferenceImportCandidate
    {
        public Guid RunId { get; init; }
        public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
        public string SourceName { get; init; } = string.Empty;
        public string SourceUrl { get; init; } = string.Empty;
        public AnimalReferenceEnvironment Environment { get; init; } = AnimalReferenceEnvironment.FreshwaterTropical;
        public string? CommonName { get; set; }
        public string? CommonNameFr { get; set; }
        public string? CommonNameEn { get; set; }
        public string? CommonNameDe { get; set; }
        public string? ScientificName { get; init; }
        public decimal? PhMin { get; init; }
        public decimal? PhMax { get; init; }
        public decimal? GhMin { get; init; }
        public decimal? GhMax { get; init; }
        public decimal? KhMin { get; init; }
        public decimal? KhMax { get; init; }
        public decimal? TemperatureMin { get; init; }
        public decimal? TemperatureMax { get; init; }
        public decimal? AmmoniaMin { get; init; }
        public decimal? AmmoniaMax { get; init; }
        public decimal? NitritesMin { get; init; }
        public decimal? NitritesMax { get; init; }
        public decimal? NitratesMin { get; init; }
        public decimal? NitratesMax { get; init; }
        public int? VolumeMinLiters { get; init; }
        public int EssentialParameterCount { get; init; }
        public string CandidateStatus { get; init; } = string.Empty;
        public string RejectionReason { get; init; } = string.Empty;
    }

    private sealed class AnimalReferenceAggregate
    {
        private readonly AnimalReference reference = new();
        private int bestSourceScore;

        public void Merge(AnimalReference source)
        {
            SanitizeAnimalReferenceForStorage(source);
            if (string.IsNullOrWhiteSpace(source.ScientificName))
            {
                return;
            }

            reference.Environment = source.Environment;
            reference.ScientificName = TrimToMax(source.ScientificName, 180);
            if (string.IsNullOrWhiteSpace(reference.CommonName)
                || string.Equals(reference.CommonName, reference.ScientificName, StringComparison.OrdinalIgnoreCase))
            {
                reference.CommonName = TrimToMax(string.IsNullOrWhiteSpace(source.CommonName) ? source.ScientificName : source.CommonName, 160);
            }

            reference.CommonNameFr = MergeText(reference.CommonNameFr, source.CommonNameFr, 160);
            reference.CommonNameEn = MergeText(reference.CommonNameEn, source.CommonNameEn, 160);
            reference.CommonNameDe = MergeText(reference.CommonNameDe, source.CommonNameDe, 160);

            (reference.PhMin, reference.PhMax) = FillRange(reference.PhMin, reference.PhMax, source.PhMin, source.PhMax);
            (reference.GhMin, reference.GhMax) = FillRange(reference.GhMin, reference.GhMax, source.GhMin, source.GhMax);
            (reference.KhMin, reference.KhMax) = FillRange(reference.KhMin, reference.KhMax, source.KhMin, source.KhMax);
            (reference.TemperatureMin, reference.TemperatureMax) = FillRange(reference.TemperatureMin, reference.TemperatureMax, source.TemperatureMin, source.TemperatureMax);
            (reference.AmmoniaMin, reference.AmmoniaMax) = FillRange(reference.AmmoniaMin, reference.AmmoniaMax, source.AmmoniaMin, source.AmmoniaMax);
            (reference.NitritesMin, reference.NitritesMax) = FillRange(reference.NitritesMin, reference.NitritesMax, source.NitritesMin, source.NitritesMax);
            (reference.NitratesMin, reference.NitratesMax) = FillRange(reference.NitratesMin, reference.NitratesMax, source.NitratesMin, source.NitratesMax);
            reference.VolumeMinLiters ??= source.VolumeMinLiters;

            if (string.IsNullOrWhiteSpace(reference.Behavior) && !string.IsNullOrWhiteSpace(source.Behavior))
            {
                reference.Behavior = TrimToMax(source.Behavior, 180);
            }

            if (string.IsNullOrWhiteSpace(reference.Compatibility) && !string.IsNullOrWhiteSpace(source.Compatibility))
            {
                reference.Compatibility = TrimToMax(source.Compatibility, 220);
            }

            var score = CountEssentialAnimalParameters(source);
            if (score >= bestSourceScore && !string.IsNullOrWhiteSpace(source.SourceUrl))
            {
                reference.SourceUrl = TrimToMax(source.SourceUrl, 512);
                bestSourceScore = score;
            }
        }

        public AnimalReference ToReference()
        {
            reference.CommonName = TrimToMax(string.IsNullOrWhiteSpace(reference.CommonName) ? reference.ScientificName : reference.CommonName, 160);
            reference.CommonNameFr = TrimToMax(reference.CommonNameFr, 160);
            reference.CommonNameEn = TrimToMax(reference.CommonNameEn, 160);
            reference.CommonNameDe = TrimToMax(reference.CommonNameDe, 160);
            reference.ScientificName = TrimToMax(reference.ScientificName, 180);
            reference.Behavior = TrimToMax(reference.Behavior, 180);
            reference.Compatibility = TrimToMax(reference.Compatibility, 220);
            reference.SourceUrl = TrimToMax(reference.SourceUrl, 512);
            return reference;
        }

        private static (decimal? Min, decimal? Max) FillRange(decimal? targetMin, decimal? targetMax, decimal? sourceMin, decimal? sourceMax)
        {
            targetMin ??= sourceMin;
            targetMax ??= sourceMax;
            if (targetMin.HasValue && targetMax.HasValue && targetMin > targetMax)
            {
                (targetMin, targetMax) = (targetMax, targetMin);
            }

            return (targetMin, targetMax);
        }

        private static string MergeText(string target, string source, int maxLength)
        {
            return string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(source)
                ? TrimToMax(source, maxLength)
                : target;
        }
    }

    private static void MergeAnimalReference(
        Dictionary<string, AnimalReferenceAggregate> aggregates,
        AnimalReference reference,
        AnimalImportCounters counters)
    {
        SanitizeAnimalReferenceForStorage(reference);
        if (!TryNormalizeScientificName(reference.ScientificName, out var scientific))
        {
            return;
        }

        reference.ScientificName = scientific;
        reference.CommonName = TrimToMax(string.IsNullOrWhiteSpace(reference.CommonName) ? scientific : reference.CommonName, 160);
        if (!aggregates.TryGetValue(scientific, out var aggregate))
        {
            aggregate = new AnimalReferenceAggregate();
            aggregates[scientific] = aggregate;
            counters.Discovered++;
        }

        aggregate.Merge(reference);
    }

    private static string TrimToMax(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static int NormalizeMinimumAnimalEssentialParameterGroups(int value)
    {
        return Math.Clamp(value, 1, 8);
    }

    private static int NormalizeMinimumReferenceParameterGroups(int value)
    {
        return Math.Clamp(value, 1, 8);
    }

    private static void RecordPlantImportCandidate(
        PlantImportCounters counters,
        string sourceName,
        string sourceUrl,
        PlantReferenceEnvironment environment,
        PlantReference? reference,
        string status,
        string reason)
    {
        if (reference is not null)
        {
            SanitizePlantReferenceForStorage(reference);
        }

        counters.Candidates.Add(new PlantReferenceImportCandidate
        {
            RunId = counters.RunId,
            CollectedAt = DateTime.UtcNow,
            SourceName = TrimToMax(sourceName, 80),
            SourceUrl = TrimToMax(sourceUrl, 512),
            Environment = reference?.Environment ?? environment,
            CommonName = string.IsNullOrWhiteSpace(reference?.CommonName) ? null : TrimToMax(reference.CommonName, 160),
            CommonNameFr = string.IsNullOrWhiteSpace(reference?.CommonNameFr) ? null : TrimToMax(reference.CommonNameFr, 160),
            CommonNameEn = string.IsNullOrWhiteSpace(reference?.CommonNameEn) ? null : TrimToMax(reference.CommonNameEn, 160),
            CommonNameDe = string.IsNullOrWhiteSpace(reference?.CommonNameDe) ? null : TrimToMax(reference.CommonNameDe, 160),
            ScientificName = string.IsNullOrWhiteSpace(reference?.ScientificName) ? null : TrimToMax(reference.ScientificName, 180),
            PhMin = reference?.PhMin,
            PhMax = reference?.PhMax,
            GhMin = reference?.GhMin,
            GhMax = reference?.GhMax,
            KhMin = reference?.KhMin,
            KhMax = reference?.KhMax,
            TemperatureMin = reference?.TemperatureMin,
            TemperatureMax = reference?.TemperatureMax,
            AmmoniaMin = reference?.AmmoniaMin,
            AmmoniaMax = reference?.AmmoniaMax,
            NitritesMin = reference?.NitritesMin,
            NitritesMax = reference?.NitritesMax,
            NitratesMin = reference?.NitratesMin,
            NitratesMax = reference?.NitratesMax,
            VolumeMinLiters = reference?.VolumeMinLiters,
            LightNeed = reference?.LightNeed,
            Co2Need = reference?.Co2Need,
            FertilizationNeed = reference?.FertilizationNeed,
            GrowthSpeed = reference?.GrowthSpeed,
            RecommendedPlacement = reference?.RecommendedPlacement,
            Behavior = reference?.Behavior,
            Compatibility = reference?.Compatibility,
            EssentialParameterCount = reference is null ? 0 : CountEssentialPlantParameters(reference),
            CandidateStatus = TrimToMax(status, 60),
            RejectionReason = TrimToMax(reason, 220)
        });
    }

    private static async Task ClearPlantReferenceImportCandidatesAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("DELETE FROM PlantReferenceImportCandidates;", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ApplyPlantCommonNamesToImportCandidates(
        PlantImportCounters counters,
        IReadOnlyList<PlantReference> references)
    {
        var namesByScientificName = references
            .Where(reference => !string.IsNullOrWhiteSpace(reference.ScientificName))
            .GroupBy(reference => reference.ScientificName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in counters.Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.ScientificName)
                || !namesByScientificName.TryGetValue(candidate.ScientificName, out var reference))
            {
                continue;
            }

            candidate.CommonNameFr = MergeCandidateText(candidate.CommonNameFr, reference.CommonNameFr);
            candidate.CommonNameEn = MergeCandidateText(candidate.CommonNameEn, reference.CommonNameEn);
            candidate.CommonNameDe = MergeCandidateText(candidate.CommonNameDe, reference.CommonNameDe);
            if (string.IsNullOrWhiteSpace(candidate.CommonName)
                || string.Equals(candidate.CommonName, candidate.ScientificName, StringComparison.OrdinalIgnoreCase))
            {
                candidate.CommonName = FirstNonEmpty(reference.CommonNameFr, reference.CommonNameEn, reference.CommonNameDe, reference.CommonName, candidate.CommonName ?? string.Empty);
            }
        }
    }

    private static async Task PersistPlantReferenceImportCandidatesAsync(
        MySqlConnection connection,
        PlantImportCounters counters,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        const string insertSql =
            """
            INSERT INTO PlantReferenceImportCandidates (
                Id, RunId, CollectedAt, SourceName, SourceUrl, Environment, CommonName, CommonNameFr, CommonNameEn, CommonNameDe, ScientificName,
                PhMin, PhMax, GhMin, GhMax, KhMin, KhMax, TemperatureMin, TemperatureMax,
                AmmoniaMin, AmmoniaMax, NitritesMin, NitritesMax, NitratesMin, NitratesMax,
                VolumeMinLiters, LightNeed, Co2Need, FertilizationNeed, GrowthSpeed, RecommendedPlacement, Behavior, Compatibility,
                EssentialParameterCount, CandidateStatus, RejectionReason)
            VALUES (
                @Id, @RunId, @CollectedAt, @SourceName, @SourceUrl, @Environment, @CommonName, @CommonNameFr, @CommonNameEn, @CommonNameDe, @ScientificName,
                @PhMin, @PhMax, @GhMin, @GhMax, @KhMin, @KhMax, @TemperatureMin, @TemperatureMax,
                @AmmoniaMin, @AmmoniaMax, @NitritesMin, @NitritesMax, @NitratesMin, @NitratesMax,
                @VolumeMinLiters, @LightNeed, @Co2Need, @FertilizationNeed, @GrowthSpeed, @RecommendedPlacement, @Behavior, @Compatibility,
                @EssentialParameterCount, @CandidateStatus, @RejectionReason);
            """;

        progress?.Report($"Plantes: journalisation de {counters.Candidates.Count} fiches candidates...");
        for (var index = 0; index < counters.Candidates.Count; index++)
        {
            var candidate = counters.Candidates[index];
            SanitizePlantReferenceImportCandidateForStorage(candidate);
            await using var insert = new MySqlCommand(insertSql, connection);
            insert.Parameters.AddRange(new[]
            {
                Parameter("@Id", Guid.NewGuid().ToString()),
                Parameter("@RunId", candidate.RunId.ToString()),
                Parameter("@CollectedAt", candidate.CollectedAt),
                Parameter("@SourceName", candidate.SourceName),
                Parameter("@SourceUrl", candidate.SourceUrl),
                Parameter("@Environment", candidate.Environment.ToString()),
                Parameter("@CommonName", candidate.CommonName),
                Parameter("@CommonNameFr", candidate.CommonNameFr),
                Parameter("@CommonNameEn", candidate.CommonNameEn),
                Parameter("@CommonNameDe", candidate.CommonNameDe),
                Parameter("@ScientificName", candidate.ScientificName),
                Parameter("@PhMin", candidate.PhMin),
                Parameter("@PhMax", candidate.PhMax),
                Parameter("@GhMin", candidate.GhMin),
                Parameter("@GhMax", candidate.GhMax),
                Parameter("@KhMin", candidate.KhMin),
                Parameter("@KhMax", candidate.KhMax),
                Parameter("@TemperatureMin", candidate.TemperatureMin),
                Parameter("@TemperatureMax", candidate.TemperatureMax),
                Parameter("@AmmoniaMin", candidate.AmmoniaMin),
                Parameter("@AmmoniaMax", candidate.AmmoniaMax),
                Parameter("@NitritesMin", candidate.NitritesMin),
                Parameter("@NitritesMax", candidate.NitritesMax),
                Parameter("@NitratesMin", candidate.NitratesMin),
                Parameter("@NitratesMax", candidate.NitratesMax),
                Parameter("@VolumeMinLiters", candidate.VolumeMinLiters),
                Parameter("@LightNeed", candidate.LightNeed),
                Parameter("@Co2Need", candidate.Co2Need),
                Parameter("@FertilizationNeed", candidate.FertilizationNeed),
                Parameter("@GrowthSpeed", candidate.GrowthSpeed),
                Parameter("@RecommendedPlacement", candidate.RecommendedPlacement),
                Parameter("@Behavior", candidate.Behavior),
                Parameter("@Compatibility", candidate.Compatibility),
                Parameter("@EssentialParameterCount", candidate.EssentialParameterCount),
                Parameter("@CandidateStatus", candidate.CandidateStatus),
                Parameter("@RejectionReason", candidate.RejectionReason)
            });
            await insert.ExecuteNonQueryAsync(cancellationToken);

            if ((index + 1) % 100 == 0 || index + 1 == counters.Candidates.Count)
            {
                progress?.Report($"Plantes: {index + 1}/{counters.Candidates.Count} fiches candidates journalisees...");
            }
        }
    }

    private static void SanitizePlantReferenceImportCandidateForStorage(PlantReferenceImportCandidate candidate)
    {
        candidate.CommonName = CapitalizeFirstLetterOrNull(candidate.CommonName, 160);
        candidate.CommonNameFr = CapitalizeFirstLetterOrNull(candidate.CommonNameFr, 160);
        candidate.CommonNameEn = CapitalizeFirstLetterOrNull(candidate.CommonNameEn, 160);
        candidate.CommonNameDe = CapitalizeFirstLetterOrNull(candidate.CommonNameDe, 160);
    }

    private static void RecordAnimalImportCandidate(
        AnimalImportCounters counters,
        string sourceName,
        string sourceUrl,
        AnimalReferenceEnvironment environment,
        AnimalReference? reference,
        string status,
        string reason)
    {
        if (reference is not null)
        {
            SanitizeAnimalReferenceForStorage(reference);
        }

        counters.Candidates.Add(new AnimalReferenceImportCandidate
        {
            RunId = counters.RunId,
            CollectedAt = DateTime.UtcNow,
            SourceName = TrimToMax(sourceName, 80),
            SourceUrl = TrimToMax(sourceUrl, 512),
            Environment = reference?.Environment ?? environment,
            CommonName = string.IsNullOrWhiteSpace(reference?.CommonName) ? null : TrimToMax(reference.CommonName, 160),
            CommonNameFr = string.IsNullOrWhiteSpace(reference?.CommonNameFr) ? null : TrimToMax(reference.CommonNameFr, 160),
            CommonNameEn = string.IsNullOrWhiteSpace(reference?.CommonNameEn) ? null : TrimToMax(reference.CommonNameEn, 160),
            CommonNameDe = string.IsNullOrWhiteSpace(reference?.CommonNameDe) ? null : TrimToMax(reference.CommonNameDe, 160),
            ScientificName = string.IsNullOrWhiteSpace(reference?.ScientificName) ? null : TrimToMax(reference.ScientificName, 180),
            PhMin = reference?.PhMin,
            PhMax = reference?.PhMax,
            GhMin = reference?.GhMin,
            GhMax = reference?.GhMax,
            KhMin = reference?.KhMin,
            KhMax = reference?.KhMax,
            TemperatureMin = reference?.TemperatureMin,
            TemperatureMax = reference?.TemperatureMax,
            AmmoniaMin = reference?.AmmoniaMin,
            AmmoniaMax = reference?.AmmoniaMax,
            NitritesMin = reference?.NitritesMin,
            NitritesMax = reference?.NitritesMax,
            NitratesMin = reference?.NitratesMin,
            NitratesMax = reference?.NitratesMax,
            VolumeMinLiters = reference?.VolumeMinLiters,
            EssentialParameterCount = reference is null ? 0 : CountEssentialAnimalParameters(reference),
            CandidateStatus = TrimToMax(status, 60),
            RejectionReason = TrimToMax(reason, 220)
        });
    }

    private static async Task ClearAnimalReferenceImportCandidatesAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("DELETE FROM AnimalReferenceImportCandidates;", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ApplyAnimalCommonNamesToImportCandidates(
        AnimalImportCounters counters,
        IReadOnlyList<AnimalReference> consolidatedReferences)
    {
        var namesByScientificName = consolidatedReferences
            .Where(reference => !string.IsNullOrWhiteSpace(reference.ScientificName))
            .GroupBy(reference => reference.ScientificName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in counters.Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.ScientificName)
                || !namesByScientificName.TryGetValue(candidate.ScientificName, out var reference))
            {
                continue;
            }

            candidate.CommonNameFr = MergeCandidateText(candidate.CommonNameFr, reference.CommonNameFr);
            candidate.CommonNameEn = MergeCandidateText(candidate.CommonNameEn, reference.CommonNameEn);
            candidate.CommonNameDe = MergeCandidateText(candidate.CommonNameDe, reference.CommonNameDe);
            if (string.IsNullOrWhiteSpace(candidate.CommonName)
                || string.Equals(candidate.CommonName, candidate.ScientificName, StringComparison.OrdinalIgnoreCase))
            {
                candidate.CommonName = FirstNonEmpty(reference.CommonNameFr, reference.CommonNameEn, reference.CommonNameDe, reference.CommonName, candidate.CommonName ?? string.Empty);
            }
        }
    }

    private static string? MergeCandidateText(string? target, string source)
    {
        return string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(source)
            ? TrimToMax(source, 160)
            : target;
    }

    private static async Task PersistAnimalReferenceImportCandidatesAsync(
        MySqlConnection connection,
        AnimalImportCounters counters,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        const string insertSql =
            """
            INSERT INTO AnimalReferenceImportCandidates (
                Id, RunId, CollectedAt, SourceName, SourceUrl, Environment, CommonName, CommonNameFr, CommonNameEn, CommonNameDe, ScientificName,
                PhMin, PhMax, GhMin, GhMax, KhMin, KhMax, TemperatureMin, TemperatureMax,
                AmmoniaMin, AmmoniaMax, NitritesMin, NitritesMax, NitratesMin, NitratesMax,
                VolumeMinLiters, EssentialParameterCount, CandidateStatus, RejectionReason)
            VALUES (
                @Id, @RunId, @CollectedAt, @SourceName, @SourceUrl, @Environment, @CommonName, @CommonNameFr, @CommonNameEn, @CommonNameDe, @ScientificName,
                @PhMin, @PhMax, @GhMin, @GhMax, @KhMin, @KhMax, @TemperatureMin, @TemperatureMax,
                @AmmoniaMin, @AmmoniaMax, @NitritesMin, @NitritesMax, @NitratesMin, @NitratesMax,
                @VolumeMinLiters, @EssentialParameterCount, @CandidateStatus, @RejectionReason);
            """;

        progress?.Report($"Population: journalisation de {counters.Candidates.Count} fiches candidates...");
        for (var index = 0; index < counters.Candidates.Count; index++)
        {
            var candidate = counters.Candidates[index];
            SanitizeAnimalReferenceImportCandidateForStorage(candidate);
            await using var insert = new MySqlCommand(insertSql, connection);
            insert.Parameters.AddRange(new[]
            {
                Parameter("@Id", Guid.NewGuid().ToString()),
                Parameter("@RunId", candidate.RunId.ToString()),
                Parameter("@CollectedAt", candidate.CollectedAt),
                Parameter("@SourceName", candidate.SourceName),
                Parameter("@SourceUrl", candidate.SourceUrl),
                Parameter("@Environment", candidate.Environment.ToString()),
                Parameter("@CommonName", candidate.CommonName),
                Parameter("@CommonNameFr", candidate.CommonNameFr),
                Parameter("@CommonNameEn", candidate.CommonNameEn),
                Parameter("@CommonNameDe", candidate.CommonNameDe),
                Parameter("@ScientificName", candidate.ScientificName),
                Parameter("@PhMin", candidate.PhMin),
                Parameter("@PhMax", candidate.PhMax),
                Parameter("@GhMin", candidate.GhMin),
                Parameter("@GhMax", candidate.GhMax),
                Parameter("@KhMin", candidate.KhMin),
                Parameter("@KhMax", candidate.KhMax),
                Parameter("@TemperatureMin", candidate.TemperatureMin),
                Parameter("@TemperatureMax", candidate.TemperatureMax),
                Parameter("@AmmoniaMin", candidate.AmmoniaMin),
                Parameter("@AmmoniaMax", candidate.AmmoniaMax),
                Parameter("@NitritesMin", candidate.NitritesMin),
                Parameter("@NitritesMax", candidate.NitritesMax),
                Parameter("@NitratesMin", candidate.NitratesMin),
                Parameter("@NitratesMax", candidate.NitratesMax),
                Parameter("@VolumeMinLiters", candidate.VolumeMinLiters),
                Parameter("@EssentialParameterCount", candidate.EssentialParameterCount),
                Parameter("@CandidateStatus", candidate.CandidateStatus),
                Parameter("@RejectionReason", candidate.RejectionReason)
            });
            await insert.ExecuteNonQueryAsync(cancellationToken);

            if ((index + 1) % 100 == 0 || index + 1 == counters.Candidates.Count)
            {
                progress?.Report($"Population: {index + 1}/{counters.Candidates.Count} fiches candidates journalisees...");
            }
        }
    }

    private static void SanitizeAnimalReferenceImportCandidateForStorage(AnimalReferenceImportCandidate candidate)
    {
        candidate.CommonName = CapitalizeFirstLetterOrNull(candidate.CommonName, 160);
        candidate.CommonNameFr = CapitalizeFirstLetterOrNull(candidate.CommonNameFr, 160);
        candidate.CommonNameEn = CapitalizeFirstLetterOrNull(candidate.CommonNameEn, 160);
        candidate.CommonNameDe = CapitalizeFirstLetterOrNull(candidate.CommonNameDe, 160);
    }

    private static async Task CollectFreshwaterWikipediaListAsync(
        HttpClient http,
        Dictionary<string, AnimalReferenceAggregate> aggregates,
        AnimalImportCounters counters,
        int minimumParameterGroupCount,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("Population: collecte Wikipedia eau douce...");
        var wikiText = await TryFetchWikipediaWikitextAsync(http, "List_of_freshwater_aquarium_fish_species", cancellationToken);
        IReadOnlyList<AnimalReference> references = string.IsNullOrWhiteSpace(wikiText)
            ? []
            : DiscoverFreshwaterAquariumFishReferencesFromWikiText(wikiText);

        if (references.Count == 0)
        {
            var html = await TryGetStringWithRetryAsync(http, FreshwaterAquariumFishListUrl, cancellationToken);
            references = string.IsNullOrWhiteSpace(html)
                ? []
                : DiscoverFreshwaterAquariumFishReferencesFromHtml(html);
        }

        foreach (var reference in references)
        {
            var score = CountEssentialAnimalParameters(reference);
            RecordAnimalImportCandidate(
                counters,
                "Wikipedia eau douce",
                reference.SourceUrl,
                reference.Environment,
                reference,
                score >= minimumParameterGroupCount ? "Candidate" : "CandidateIncomplete",
                score >= minimumParameterGroupCount ? string.Empty : BuildAnimalCandidateRejectionReason(reference, minimumParameterGroupCount));
            MergeAnimalReference(aggregates, reference, counters);
        }

        progress?.Report($"Population: Wikipedia eau douce consolide ({references.Count} fiches, {aggregates.Count} candidates).");
    }

    private static async Task CollectFishFishReferencesAsync(
        HttpClient http,
        Dictionary<string, AnimalReferenceAggregate> aggregates,
        AnimalImportCounters counters,
        int minimumParameterGroupCount,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var urls = Enumerable.Range(1, 45)
            .Select(page => page == 1 ? "https://www.fishfish.fr/poisson" : $"https://www.fishfish.fr/poisson/{page}")
            .ToList();

        await CollectProfileDirectoryPagesAsync(
            http,
            aggregates,
            counters,
            progress,
            urls,
            "FishFish",
            "https://www.fishfish.fr",
            @"href=[""'](?<href>/poisson/[a-z0-9\-]+)[""']",
            AnimalReferenceEnvironment.FreshwaterTropical,
            1200,
            minimumParameterGroupCount,
            cancellationToken);
    }

    private static async Task CollectFishipediaReferencesAsync(
        HttpClient http,
        Dictionary<string, AnimalReferenceAggregate> aggregates,
        AnimalImportCounters counters,
        int minimumParameterGroupCount,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var urls = Enumerable.Range(1, 12)
            .Select(page => page == 1 ? "https://www.fishipedia.fr/poissons/" : $"https://www.fishipedia.fr/poissons/page/{page}/")
            .ToList();

        await CollectProfileDirectoryPagesAsync(
            http,
            aggregates,
            counters,
            progress,
            urls,
            "Fishipedia",
            "https://www.fishipedia.fr",
            @"href=[""'](?<href>/(?:fr/)?poissons/[a-z0-9\-]+/?(?:\?[^""']*)?)[""']",
            AnimalReferenceEnvironment.FreshwaterTropical,
            800,
            minimumParameterGroupCount,
            cancellationToken);
    }

    private static async Task CollectLiveAquariaReferencesAsync(
        HttpClient http,
        Dictionary<string, AnimalReferenceAggregate> aggregates,
        AnimalImportCounters counters,
        int minimumParameterGroupCount,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var marineUrls = new[]
        {
            "https://www.liveaquaria.com/category/15/marine-fish"
        };

        await CollectProfileDirectoryPagesAsync(
            http,
            aggregates,
            counters,
            progress,
            marineUrls,
            "LiveAquaria eau de mer",
            "https://www.liveaquaria.com",
            @"href=[""'](?<href>/(?:product|products)/[^""']+)[""']",
            AnimalReferenceEnvironment.Marine,
            250,
            minimumParameterGroupCount,
            cancellationToken);

        var freshwaterUrls = new[]
        {
            "https://www.liveaquaria.com/category/830/freshwater-fish"
        };

        await CollectProfileDirectoryPagesAsync(
            http,
            aggregates,
            counters,
            progress,
            freshwaterUrls,
            "LiveAquaria eau douce",
            "https://www.liveaquaria.com",
            @"href=[""'](?<href>/(?:product|products)/[^""']+)[""']",
            AnimalReferenceEnvironment.FreshwaterTropical,
            250,
            minimumParameterGroupCount,
            cancellationToken);
    }

    private static async Task CollectProfileDirectoryPagesAsync(
        HttpClient http,
        Dictionary<string, AnimalReferenceAggregate> aggregates,
        AnimalImportCounters counters,
        IProgress<string>? progress,
        IReadOnlyList<string> directoryUrls,
        string sourceName,
        string baseUrl,
        string hrefPattern,
        AnimalReferenceEnvironment defaultEnvironment,
        int maxProfileLinks,
        int minimumParameterGroupCount,
        CancellationToken cancellationToken)
    {
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directoryUrl in directoryUrls)
        {
            var html = await TryGetStringWithRetryAsync(http, directoryUrl, cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
            {
                continue;
            }

            foreach (var link in ExtractLinks(html, hrefPattern, baseUrl))
            {
                links.Add(link);
                if (links.Count >= maxProfileLinks)
                {
                    break;
                }
            }

            if (links.Count >= maxProfileLinks)
            {
                break;
            }
        }

        progress?.Report($"Population: {sourceName} - {links.Count} pages candidates trouvees.");
        var orderedLinks = links.OrderBy(link => link).ToList();
        for (var index = 0; index < orderedLinks.Count; index += 100)
        {
            var batch = orderedLinks.Skip(index).Take(100).ToList();
            progress?.Report($"Population: {sourceName} - analyse paquet {(index / 100) + 1} ({batch.Count} pages)...");
            foreach (var link in batch)
            {
                var html = await TryGetStringWithRetryAsync(http, link, cancellationToken);
                if (string.IsNullOrWhiteSpace(html))
                {
                    RecordAnimalImportCandidate(counters, sourceName, link, defaultEnvironment, null, "FetchFailed", "Page candidate inaccessible.");
                    continue;
                }

                var reference = TryBuildAnimalReferenceFromProfilePage(link, html, defaultEnvironment, out var rejectionReason);
                if (reference is not null)
                {
                    var score = CountEssentialAnimalParameters(reference);
                    RecordAnimalImportCandidate(
                        counters,
                        sourceName,
                        link,
                        defaultEnvironment,
                        reference,
                        score >= minimumParameterGroupCount ? "Candidate" : "CandidateIncomplete",
                        score >= minimumParameterGroupCount ? string.Empty : BuildAnimalCandidateRejectionReason(reference, minimumParameterGroupCount));
                    MergeAnimalReference(aggregates, reference, counters);
                }
                else
                {
                    RecordAnimalImportCandidate(counters, sourceName, link, defaultEnvironment, null, "Rejected", rejectionReason);
                }
            }

            progress?.Report($"Population: {sourceName} - paquet {(index / 100) + 1} consolide ({aggregates.Count} candidates).");
        }
    }

    private static async Task CollectWikipediaCategoryReferencesAsync(
        HttpClient http,
        Dictionary<string, AnimalReferenceAggregate> aggregates,
        string category,
        AnimalReferenceEnvironment environment,
        AnimalImportCounters counters,
        int minimumParameterGroupCount,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var continueToken = string.Empty;
        var categoryBatch = 1;
        var categoryDedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var continuePart = string.IsNullOrWhiteSpace(continueToken) ? string.Empty : $"&cmcontinue={Uri.EscapeDataString(continueToken)}";
            var apiUrl = $"https://en.wikipedia.org/w/api.php?action=query&list=categorymembers&cmtitle=Category:{category}&cmlimit=100&format=json{continuePart}";
            progress?.Report($"Population: Wikipedia categorie {category} - paquet {categoryBatch}...");
            var json = await TryGetStringWithRetryAsync(http, apiUrl, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                break;
            }

            string? nextContinueToken = null;
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("query", out var queryElement)
                    && queryElement.TryGetProperty("categorymembers", out var members)
                    && members.ValueKind == JsonValueKind.Array)
                {
                    foreach (var member in members.EnumerateArray())
                    {
                        var candidateUrl = string.Empty;
                        if (member.TryGetProperty("title", out var titleElement))
                        {
                            var title = titleElement.GetString() ?? string.Empty;
                            candidateUrl = string.IsNullOrWhiteSpace(title)
                                ? string.Empty
                                : $"https://en.wikipedia.org/wiki/{title.Replace(' ', '_')}";
                        }

                        var reference = await TryCreateAnimalReferenceFromWikipediaMemberAsync(http, member, environment, categoryDedup, minimumParameterGroupCount, cancellationToken);
                        if (reference is not null)
                        {
                            var score = CountEssentialAnimalParameters(reference);
                            RecordAnimalImportCandidate(
                                counters,
                                $"Wikipedia {category}",
                                reference.SourceUrl,
                                environment,
                                reference,
                                score >= minimumParameterGroupCount ? "Candidate" : "CandidateIncomplete",
                                score >= minimumParameterGroupCount ? string.Empty : BuildAnimalCandidateRejectionReason(reference, minimumParameterGroupCount));
                            MergeAnimalReference(aggregates, reference, counters);
                        }
                        else if (!string.IsNullOrWhiteSpace(candidateUrl))
                        {
                            RecordAnimalImportCandidate(counters, $"Wikipedia {category}", candidateUrl, environment, null, "Rejected", "Membre de categorie non exploitable.");
                        }
                    }
                }

                if (document.RootElement.TryGetProperty("continue", out var continueElement)
                    && continueElement.TryGetProperty("cmcontinue", out var cmcontinueElement))
                {
                    nextContinueToken = cmcontinueElement.GetString();
                }
            }
            catch
            {
                counters.Failed++;
                break;
            }

            progress?.Report($"Population: Wikipedia categorie {category} - paquet {categoryBatch} consolide ({aggregates.Count} candidates).");
            categoryBatch++;
            if (string.IsNullOrWhiteSpace(nextContinueToken))
            {
                break;
            }

            continueToken = nextContinueToken;
        }
    }

    private static async Task ImportFreshwaterWikipediaListByBatchesAsync(
        MySqlConnection connection,
        string existsSql,
        string insertSql,
        HashSet<string> dedup,
        AnimalImportCounters counters,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var http = CreateWikiHttpClient();
        progress?.Report("Population: lecture du referentiel Wikipedia eau douce...");
        var html = await TryGetStringWithRetryAsync(http, FreshwaterAquariumFishListUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            counters.Failed++;
            return;
        }

        var references = DiscoverFreshwaterAquariumFishReferencesFromHtml(html);
        progress?.Report($"Population: referentiel Wikipedia eau douce analyse ({references.Count} especes avec au moins {MinimumAnimalEssentialParameterCount} parametres).");
        for (var index = 0; index < references.Count; index += 100)
        {
            var batch = references
                .Skip(index)
                .Take(100)
                .Where(reference => dedup.Add(reference.ScientificName))
                .ToList();
            if (batch.Count == 0)
            {
                continue;
            }

            progress?.Report($"Population: paquet {counters.BatchIndex} trouve ({batch.Count} especes depuis Wikipedia), insertion...");
            await InsertAnimalReferenceBatchAsync(connection, existsSql, insertSql, batch, counters, MinimumAnimalEssentialParameterCount, progress, cancellationToken);
            progress?.Report($"Population: paquet {counters.BatchIndex} termine (ajoutees: {counters.Inserted}, ignorees: {counters.Skipped}, erreurs: {counters.Failed}).");
            counters.BatchIndex++;
        }
    }

    private static IReadOnlyList<AnimalReference> DiscoverFreshwaterAquariumFishReferencesFromWikiText(string wikiText)
    {
        var references = new Dictionary<string, AnimalReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Regex.Split(wikiText, @"\n\|-", RegexOptions.Multiline))
        {
            var cells = ExtractWikiTableCells(row);
            if (cells.Count < 8)
            {
                continue;
            }

            var commonName = CleanWikiText(cells[0]);
            var scientific = ExtractScientificNameFromText(CleanWikiText(cells[1]));
            if (!TryNormalizeScientificName(scientific, out var normalizedScientific))
            {
                continue;
            }

            var reference = new AnimalReference
            {
                Environment = AnimalReferenceEnvironment.FreshwaterTropical,
                CommonName = string.IsNullOrWhiteSpace(commonName) ? normalizedScientific : commonName,
                CommonNameEn = string.IsNullOrWhiteSpace(commonName) ? string.Empty : commonName,
                ScientificName = normalizedScientific,
                SourceUrl = FreshwaterAquariumFishListUrl
            };

            if (cells.Count > 5)
            {
                reference.VolumeMinLiters = ExtractVolumeLitersFromText(CleanWikiText(cells[5]));
            }

            if (cells.Count > 6)
            {
                var temperature = ExtractTemperatureRangeFromText(CleanWikiText(cells[6]));
                if (temperature is not null)
                {
                    reference.TemperatureMin = temperature.Value.Min;
                    reference.TemperatureMax = temperature.Value.Max;
                }
            }

            if (cells.Count > 7)
            {
                var ph = ExtractWaterRangeFromText(CleanWikiText(cells[7]), 0m, 14m);
                if (ph is not null)
                {
                    reference.PhMin = ph.Value.Min;
                    reference.PhMax = ph.Value.Max;
                }
            }

            if (cells.Count > 8)
            {
                var hardness = ExtractHardnessRangeFromText(CleanWikiText(cells[8]));
                if (hardness is not null)
                {
                    reference.GhMin = hardness.Value.Min;
                    reference.GhMax = hardness.Value.Max;
                }
            }

            SanitizeAnimalReferenceForStorage(reference);
            references.TryAdd(reference.ScientificName, reference);
        }

        return references.Values.OrderBy(reference => reference.ScientificName).ToList();
    }

    private static List<string> ExtractWikiTableCells(string row)
    {
        var cells = new List<string>();
        foreach (var rawLine in row.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('|') || line.StartsWith("|-") || line.StartsWith("|}"))
            {
                continue;
            }

            var content = line[1..].Trim();
            cells.AddRange(content.Split("||", StringSplitOptions.None).Select(cell => cell.Trim()));
        }

        return cells;
    }

    private static string CleanWikiText(string text)
    {
        var cleaned = Regex.Replace(text, @"<ref\b[^>]*>.*?</ref>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        cleaned = Regex.Replace(cleaned, @"<[^>]+>", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\{\{.*?\}\}", " ", RegexOptions.Singleline);
        cleaned = Regex.Replace(cleaned, @"\[\[(?:[^|\]]+\|)?([^\]]+)\]\]", "$1");
        cleaned = cleaned.Replace("''", string.Empty).Replace("&nbsp;", " ");
        return Regex.Replace(WebUtility.HtmlDecode(cleaned), @"\s+", " ").Trim();
    }

    private static IReadOnlyList<AnimalReference> DiscoverFreshwaterAquariumFishReferencesFromHtml(string html)
    {
        var references = new Dictionary<string, AnimalReference>(StringComparer.OrdinalIgnoreCase);
        var rows = Regex.Matches(html, @"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match row in rows)
        {
            var cells = Regex.Matches(row.Groups[1].Value, @"<t[dh]\b[^>]*>(.*?)</t[dh]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .ToList();
            if (cells.Count < 8)
            {
                continue;
            }

            var commonName = CleanHtmlText(cells[0]);
            var scientific = ExtractScientificNameFromHtmlCell(cells[1]);
            if (string.IsNullOrWhiteSpace(scientific))
            {
                continue;
            }

            var parts = scientific.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !LooksLikeScientificName(parts[0], parts[1]))
            {
                continue;
            }

            var reference = new AnimalReference
            {
                Environment = AnimalReferenceEnvironment.FreshwaterTropical,
                CommonName = string.IsNullOrWhiteSpace(commonName) ? scientific : commonName,
                CommonNameEn = string.IsNullOrWhiteSpace(commonName) ? string.Empty : commonName,
                ScientificName = NormalizeScientificName(parts[0], parts[1]),
                SourceUrl = FreshwaterAquariumFishListUrl
            };

            if (cells.Count > 5)
            {
                reference.VolumeMinLiters = ExtractVolumeLitersFromText(CleanHtmlText(cells[5]));
            }

            if (cells.Count > 6)
            {
                var temperature = ExtractWaterRangeFromText(CleanHtmlText(cells[6]), 0m, 40m);
                if (temperature is not null)
                {
                    reference.TemperatureMin = temperature.Value.Min;
                    reference.TemperatureMax = temperature.Value.Max;
                }
            }

            if (cells.Count > 7)
            {
                var ph = ExtractWaterRangeFromText(CleanHtmlText(cells[7]), 0m, 14m);
                if (ph is not null)
                {
                    reference.PhMin = ph.Value.Min;
                    reference.PhMax = ph.Value.Max;
                }
            }

            if (cells.Count > 8)
            {
                var hardness = ExtractHardnessRangeFromText(CleanHtmlText(cells[8]));
                if (hardness is not null)
                {
                    reference.GhMin = hardness.Value.Min;
                    reference.GhMax = hardness.Value.Max;
                }
            }

            SanitizeAnimalReferenceForStorage(reference);
            references.TryAdd(reference.ScientificName, reference);
        }

        return references.Values.OrderBy(reference => reference.ScientificName).ToList();
    }

    private static async Task ImportWikipediaCategoryByBatchesAsync(
        MySqlConnection connection,
        string existsSql,
        string insertSql,
        HashSet<string> dedup,
        string category,
        AnimalReferenceEnvironment environment,
        AnimalImportCounters counters,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var continueToken = string.Empty;
        using var http = CreateWikiHttpClient();

        while (true)
        {
            var continuePart = string.IsNullOrWhiteSpace(continueToken) ? string.Empty : $"&cmcontinue={Uri.EscapeDataString(continueToken)}";
            var apiUrl = $"https://en.wikipedia.org/w/api.php?action=query&list=categorymembers&cmtitle=Category:{category}&cmlimit=100&format=json{continuePart}";
            progress?.Report($"Population: recherche web paquet {counters.BatchIndex} ({category})...");
            var json = await TryGetStringWithRetryAsync(http, apiUrl, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                break;
            }

            string? nextContinueToken = null;
            var batch = new List<AnimalReference>();
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("query", out var queryElement)
                    && queryElement.TryGetProperty("categorymembers", out var members)
                    && members.ValueKind == JsonValueKind.Array)
                {
                    foreach (var member in members.EnumerateArray())
                    {
                        var reference = await TryCreateAnimalReferenceFromWikipediaMemberAsync(http, member, environment, dedup, MinimumAnimalEssentialParameterCount, cancellationToken);
                        if (reference is not null)
                        {
                            batch.Add(reference);
                        }
                    }
                }

                if (document.RootElement.TryGetProperty("continue", out var continueElement)
                    && continueElement.TryGetProperty("cmcontinue", out var cmcontinueElement))
                {
                    nextContinueToken = cmcontinueElement.GetString();
                }
            }
            catch
            {
                counters.Failed++;
                break;
            }

            progress?.Report($"Population: paquet {counters.BatchIndex} trouve ({batch.Count} especes), insertion...");
            await InsertAnimalReferenceBatchAsync(connection, existsSql, insertSql, batch, counters, MinimumAnimalEssentialParameterCount, progress, cancellationToken);
            progress?.Report($"Population: paquet {counters.BatchIndex} termine (ajoutees: {counters.Inserted}, erreurs: {counters.Failed}).");
            counters.BatchIndex++;

            if (string.IsNullOrWhiteSpace(nextContinueToken))
            {
                break;
            }

            continueToken = nextContinueToken;
        }
    }

    private static async Task<AnimalReference?> TryCreateAnimalReferenceFromWikipediaMemberAsync(
        HttpClient http,
        JsonElement member,
        AnimalReferenceEnvironment environment,
        HashSet<string> dedup,
        int minimumParameterGroupCount,
        CancellationToken cancellationToken)
    {
        if (!member.TryGetProperty("title", out var titleElement))
        {
            return null;
        }

        var title = titleElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title) || title.Contains(':'))
        {
            return null;
        }

        var parts = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Length > 2)
        {
            return null;
        }

        var genus = parts[0];
        var species = parts[1];
        if (!LooksLikeScientificName(genus, species))
        {
            return null;
        }

        var scientific = NormalizeScientificName(genus, species);
        if (dedup.Contains(scientific))
        {
            return null;
        }

        var reference = new AnimalReference
        {
            Environment = environment,
            CommonName = scientific,
            ScientificName = scientific,
            SourceUrl = $"https://en.wikipedia.org/wiki/{title.Replace(' ', '_')}"
        };
        var enrichedFromReference = await TryEnrichAnimalReferenceFromSeriouslyFishAsync(http, reference, cancellationToken);
        if (!enrichedFromReference || CountEssentialAnimalParameters(reference) < minimumParameterGroupCount)
        {
            await EnrichAnimalReferenceFromWikipediaAsync(http, title, reference, cancellationToken);
        }

        SanitizeAnimalReferenceForStorage(reference);
        dedup.Add(scientific);
        return reference;
    }

    private static async Task InsertAnimalReferenceBatchAsync(
        MySqlConnection connection,
        string existsSql,
        string insertSql,
        IReadOnlyList<AnimalReference> batch,
        AnimalImportCounters counters,
        int minimumParameterGroupCount,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var animal in batch)
        {
            counters.Processed++;
            SanitizeAnimalReferenceForStorage(animal);
            if (CountEssentialAnimalParameters(animal) < minimumParameterGroupCount)
            {
                counters.Incomplete++;
                counters.Skipped++;
                continue;
            }

            try
            {
                await using var existsCommand = new MySqlCommand(existsSql, connection);
                existsCommand.Parameters.AddWithValue("@ScientificName", animal.ScientificName);
                var exists = Convert.ToInt32(await existsCommand.ExecuteScalarAsync(cancellationToken)) > 0;
                if (exists)
                {
                    if (await UpdateExistingAnimalReferenceCommonNamesAsync(connection, animal, cancellationToken))
                    {
                        counters.ExistingUpdated++;
                    }

                    counters.Existing++;
                    counters.Skipped++;
                    continue;
                }

                await using var insert = new MySqlCommand(insertSql, connection);
                insert.Parameters.AddRange(new[]
                {
                    Parameter("@Id", animal.Id.ToString()),
                Parameter("@Environment", animal.Environment.ToString()),
                Parameter("@CommonName", animal.CommonName),
                Parameter("@CommonNameFr", animal.CommonNameFr),
                Parameter("@CommonNameEn", animal.CommonNameEn),
                Parameter("@CommonNameDe", animal.CommonNameDe),
                Parameter("@ScientificName", animal.ScientificName),
                    Parameter("@PhMin", animal.PhMin),
                    Parameter("@PhMax", animal.PhMax),
                    Parameter("@GhMin", animal.GhMin),
                    Parameter("@GhMax", animal.GhMax),
                    Parameter("@KhMin", animal.KhMin),
                    Parameter("@KhMax", animal.KhMax),
                    Parameter("@TemperatureMin", animal.TemperatureMin),
                    Parameter("@TemperatureMax", animal.TemperatureMax),
                    Parameter("@AmmoniaMin", animal.AmmoniaMin),
                    Parameter("@AmmoniaMax", animal.AmmoniaMax),
                    Parameter("@NitritesMin", animal.NitritesMin),
                    Parameter("@NitritesMax", animal.NitritesMax),
                    Parameter("@NitratesMin", animal.NitratesMin),
                    Parameter("@NitratesMax", animal.NitratesMax),
                    Parameter("@VolumeMinLiters", animal.VolumeMinLiters),
                    Parameter("@Behavior", animal.Behavior),
                    Parameter("@Compatibility", animal.Compatibility),
                    Parameter("@SourceUrl", animal.SourceUrl)
                });
                await insert.ExecuteNonQueryAsync(cancellationToken);
                counters.Inserted++;
            }
            catch
            {
                counters.Failed++;
            }

            if (counters.Processed % 10 == 0)
            {
                progress?.Report($"Population: traitement {counters.Processed} especes (ajoutees: {counters.Inserted}, deja presentes: {counters.Existing}, incompletes: {counters.Incomplete}, erreurs: {counters.Failed})...");
            }
        }
    }

    private static async Task InsertPlantReferenceBatchAsync(
        MySqlConnection connection,
        string existsSql,
        string insertSql,
        IReadOnlyList<PlantReference> batch,
        PlantImportCounters counters,
        int minimumParameterGroupCount,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var plant in batch)
        {
            counters.Processed++;
            SanitizePlantReferenceForStorage(plant);
            if (CountEssentialPlantParameters(plant) < minimumParameterGroupCount)
            {
                counters.Incomplete++;
                counters.Skipped++;
                continue;
            }

            try
            {
                await using var existsCommand = new MySqlCommand(existsSql, connection);
                existsCommand.Parameters.AddWithValue("@ScientificName", plant.ScientificName);
                var exists = Convert.ToInt32(await existsCommand.ExecuteScalarAsync(cancellationToken)) > 0;
                if (exists)
                {
                    counters.Existing++;
                    counters.Skipped++;
                    continue;
                }

                await using var insert = new MySqlCommand(insertSql, connection);
                insert.Parameters.AddRange(new[]
                {
                    Parameter("@Id", plant.Id.ToString()),
                    Parameter("@Environment", plant.Environment.ToString()),
                    Parameter("@CommonName", plant.CommonName),
                    Parameter("@CommonNameFr", plant.CommonNameFr),
                    Parameter("@CommonNameEn", plant.CommonNameEn),
                    Parameter("@CommonNameDe", plant.CommonNameDe),
                    Parameter("@ScientificName", plant.ScientificName),
                    Parameter("@PhMin", plant.PhMin),
                    Parameter("@PhMax", plant.PhMax),
                    Parameter("@GhMin", plant.GhMin),
                    Parameter("@GhMax", plant.GhMax),
                    Parameter("@KhMin", plant.KhMin),
                    Parameter("@KhMax", plant.KhMax),
                    Parameter("@TemperatureMin", plant.TemperatureMin),
                    Parameter("@TemperatureMax", plant.TemperatureMax),
                    Parameter("@AmmoniaMin", plant.AmmoniaMin),
                    Parameter("@AmmoniaMax", plant.AmmoniaMax),
                    Parameter("@NitritesMin", plant.NitritesMin),
                    Parameter("@NitritesMax", plant.NitritesMax),
                    Parameter("@NitratesMin", plant.NitratesMin),
                    Parameter("@NitratesMax", plant.NitratesMax),
                    Parameter("@VolumeMinLiters", plant.VolumeMinLiters),
                    Parameter("@LightNeed", plant.LightNeed),
                    Parameter("@Co2Need", plant.Co2Need),
                    Parameter("@FertilizationNeed", plant.FertilizationNeed),
                    Parameter("@GrowthSpeed", plant.GrowthSpeed),
                    Parameter("@RecommendedPlacement", plant.RecommendedPlacement),
                    Parameter("@Behavior", plant.Behavior),
                    Parameter("@Compatibility", plant.Compatibility),
                    Parameter("@SourceUrl", plant.SourceUrl)
                });
                await insert.ExecuteNonQueryAsync(cancellationToken);
                counters.Inserted++;
            }
            catch
            {
                counters.Failed++;
            }

            if (counters.Processed % 10 == 0)
            {
                progress?.Report($"Plantes: traitement {counters.Processed} plantes (ajoutees: {counters.Inserted}, deja presentes: {counters.Existing}, incompletes: {counters.Incomplete}, erreurs: {counters.Failed})...");
            }
        }
    }

    private static async Task<bool> UpdateExistingAnimalReferenceCommonNamesAsync(
        MySqlConnection connection,
        AnimalReference animal,
        CancellationToken cancellationToken)
    {
        var shouldUpdateTemperature = await ShouldUpdateExistingAnimalReferenceTemperatureAsync(connection, animal, cancellationToken);
        const string sql =
            """
            UPDATE AnimalReferences
            SET CommonNameFr = COALESCE(NULLIF(CommonNameFr, ''), NULLIF(@CommonNameFr, '')),
                CommonNameEn = COALESCE(NULLIF(CommonNameEn, ''), NULLIF(@CommonNameEn, '')),
                CommonNameDe = COALESCE(NULLIF(CommonNameDe, ''), NULLIF(@CommonNameDe, '')),
                CommonName = CASE
                    WHEN CommonName IS NULL OR CommonName = '' OR CommonName = ScientificName
                        THEN COALESCE(NULLIF(@CommonNameFr, ''), NULLIF(@CommonNameEn, ''), NULLIF(@CommonNameDe, ''), CommonName)
                    ELSE CommonName
                END,
                TemperatureMin = CASE
                    WHEN @ShouldUpdateTemperature
                        THEN @TemperatureMin
                    ELSE TemperatureMin
                END,
                TemperatureMax = CASE
                    WHEN @ShouldUpdateTemperature
                        THEN @TemperatureMax
                    ELSE TemperatureMax
                END
            WHERE ScientificName = @ScientificName;
            """;

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddRange(new[]
        {
            Parameter("@ScientificName", animal.ScientificName),
            Parameter("@CommonNameFr", animal.CommonNameFr),
            Parameter("@CommonNameEn", animal.CommonNameEn),
            Parameter("@CommonNameDe", animal.CommonNameDe),
            Parameter("@TemperatureMin", animal.TemperatureMin),
            Parameter("@TemperatureMax", animal.TemperatureMax),
            Parameter("@ShouldUpdateTemperature", shouldUpdateTemperature)
        });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task<bool> ShouldUpdateExistingAnimalReferenceTemperatureAsync(
        MySqlConnection connection,
        AnimalReference animal,
        CancellationToken cancellationToken)
    {
        if (!animal.TemperatureMin.HasValue
            || !animal.TemperatureMax.HasValue
            || animal.TemperatureMin.Value == animal.TemperatureMax.Value)
        {
            return false;
        }

        await using var command = new MySqlCommand(
            "SELECT TemperatureMin, TemperatureMax FROM AnimalReferences WHERE ScientificName = @ScientificName LIMIT 1;",
            connection);
        command.Parameters.AddWithValue("@ScientificName", animal.ScientificName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        var currentMin = ReadNullableDecimal(reader, 0);
        var currentMax = ReadNullableDecimal(reader, 1);
        return !currentMin.HasValue
            || !currentMax.HasValue
            || currentMin.Value == currentMax.Value
            || currentMax.Value < currentMin.Value;
    }

    private static async Task<IReadOnlyList<IReadOnlyList<AnimalReference>>> DiscoverAnimalReferenceBatchesFromWebAsync(CancellationToken cancellationToken)
    {
        var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batches = new List<IReadOnlyList<AnimalReference>>();

        await FetchWikipediaCategoryBatchesAsync(
            batches,
            dedup,
            "Freshwater_aquarium_fish",
            AnimalReferenceEnvironment.FreshwaterTropical,
            cancellationToken);
        await FetchWikipediaCategoryBatchesAsync(
            batches,
            dedup,
            "Marine_aquarium_fish",
            AnimalReferenceEnvironment.Marine,
            cancellationToken);

        return batches;
    }

    private static async Task FetchWikipediaCategoryBatchesAsync(
        List<IReadOnlyList<AnimalReference>> batches,
        HashSet<string> dedup,
        string category,
        AnimalReferenceEnvironment environment,
        CancellationToken cancellationToken)
    {
        var continueToken = string.Empty;
        using var http = CreateWikiHttpClient();

        while (true)
        {
            var continuePart = string.IsNullOrWhiteSpace(continueToken) ? string.Empty : $"&cmcontinue={Uri.EscapeDataString(continueToken)}";
            var apiUrl = $"https://en.wikipedia.org/w/api.php?action=query&list=categorymembers&cmtitle=Category:{category}&cmlimit=100&format=json{continuePart}";
            var json = await TryGetStringWithRetryAsync(http, apiUrl, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                break;
            }

            var batch = new List<AnimalReference>();
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("query", out var queryElement)
                    && queryElement.TryGetProperty("categorymembers", out var members)
                    && members.ValueKind == JsonValueKind.Array)
                {
                    foreach (var member in members.EnumerateArray())
                    {
                        if (!member.TryGetProperty("title", out var titleElement))
                        {
                            continue;
                        }

                        var title = titleElement.GetString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(title) || title.Contains(':'))
                        {
                            continue;
                        }

                        var parts = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2)
                        {
                            continue;
                        }

                        var genus = parts[0];
                        var species = parts[1];
                        if (!LooksLikeScientificName(genus, species))
                        {
                            continue;
                        }

                        var scientific = NormalizeScientificName(genus, species);
                        if (!dedup.Add(scientific))
                        {
                            continue;
                        }

                        var reference = new AnimalReference
                        {
                            Environment = environment,
                            CommonName = scientific,
                            ScientificName = scientific,
                            SourceUrl = $"https://en.wikipedia.org/wiki/{title.Replace(' ', '_')}"
                        };
                        await EnrichAnimalReferenceFromWikipediaAsync(http, title, reference, cancellationToken);
                        batch.Add(reference);
                    }
                }

                if (batch.Count > 0)
                {
                    batches.Add(batch);
                }

                if (document.RootElement.TryGetProperty("continue", out var continueElement)
                    && continueElement.TryGetProperty("cmcontinue", out var cmcontinueElement))
                {
                    continueToken = cmcontinueElement.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(continueToken))
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }
            catch
            {
                break;
            }
        }
    }

    private static AnimalReference? TryBuildAnimalReferenceFromProfilePage(
        string url,
        string html,
        AnimalReferenceEnvironment defaultEnvironment,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;
        var text = CleanHtmlText(html);
        var pageHeading = ExtractPageHeading(html);
        var scientific = ExtractScientificNameFromPageHeading(pageHeading)
            ?? ExtractScientificNameFromSlug(url)
            ?? ExtractScientificNameFromLabeledProfileText(text)
            ?? ExtractScientificNameFromProfileHtml(html);
        if (!TryNormalizeScientificName(scientific, out var normalizedScientific))
        {
            rejectionReason = BuildInvalidScientificNameRejectionReason(scientific, html, text);
            return null;
        }

        var reference = new AnimalReference
        {
            Environment = InferAnimalEnvironment(url, html, normalizedScientific, defaultEnvironment),
            CommonName = ExtractCommonNameFromProfilePage(url, pageHeading, normalizedScientific),
            ScientificName = normalizedScientific,
            SourceUrl = url
        };
        AssignLocalizedCommonNameFromSource(reference, url);

        PopulateAnimalParametersFromText(reference, text);
        SanitizeAnimalReferenceForStorage(reference);
        return reference;
    }

    private static string BuildInvalidScientificNameRejectionReason(string? scientific, string html, string text)
    {
        var rejectedCandidate = FirstNonEmpty(
            scientific ?? string.Empty,
            ExtractRejectedScientificNameCandidateFromProfileHtml(html) ?? string.Empty,
            ExtractRejectedScientificNameCandidateFromText(text) ?? string.Empty);

        return string.IsNullOrWhiteSpace(rejectedCandidate)
            ? "Nom scientifique incorrect ou absent."
            : TrimToMax($"Nom scientifique incorrect: {rejectedCandidate}.", 220);
    }

    private static string? ExtractRejectedScientificNameCandidateFromProfileHtml(string html)
    {
        foreach (Match match in Regex.Matches(html, @"<(?:i|em)\b[^>]*>(.*?)</(?:i|em)>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var candidate = ExtractRejectedScientificNameCandidateFromText(CleanHtmlText(match.Groups[1].Value));
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ExtractRejectedScientificNameCandidateFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (Match match in Regex.Matches(text, @"\b([A-Z][a-z]{2,})\s([a-z][a-z\-]{2,})\b"))
        {
            var genus = match.Groups[1].Value;
            var species = match.Groups[2].Value;
            if (!LooksLikeScientificName(genus, species))
            {
                return $"{genus} {species}";
            }
        }

        return null;
    }

    private static void PopulateAnimalParametersFromText(AnimalReference reference, string text)
    {
        var temperature = ExtractTemperatureRangeFromText(text, explicitTemperatureRangeOnly: true)
            ?? ExtractTemperatureRangeFromText(ExtractParameterSegment(text, "temperature", "temperatures", "temperature range", "temperature de l'eau"));
        if (temperature is not null)
        {
            reference.TemperatureMin = temperature.Value.Min;
            reference.TemperatureMax = temperature.Value.Max;
        }

        var ph = ExtractWaterRangeFromText(ExtractParameterSegment(text, "ph", "acidity", "acidite"), 0m, 14m);
        if (ph is not null)
        {
            reference.PhMin = ph.Value.Min;
            reference.PhMax = ph.Value.Max;
        }

        var hardness = ExtractHardnessRangeFromText(ExtractParameterSegment(text, "hardness", "durete", "gh", "dgh"));
        if (hardness is not null)
        {
            reference.GhMin = hardness.Value.Min;
            reference.GhMax = hardness.Value.Max;
        }

        var kh = ExtractHardnessRangeFromText(ExtractParameterSegment(text, "kh", "dkh", "carbonate hardness", "alkalinity", "alcalinite"));
        if (kh is not null)
        {
            reference.KhMin = kh.Value.Min;
            reference.KhMax = kh.Value.Max;
        }

        var volumeText = ExtractParameterSegment(text, "aquarium size", "minimum tank size", "tank size", "minimum aquarium size", "volume", "volume minimum", "aquarium minimum");
        reference.VolumeMinLiters ??= ExtractVolumeLitersFromText(volumeText);

        var nitrateRange = ExtractWaterRangeFromText(ExtractParameterSegment(text, "nitrate", "nitrates", "no3"), 0m, 1000m);
        if (nitrateRange is not null)
        {
            reference.NitratesMin = nitrateRange.Value.Min;
            reference.NitratesMax = nitrateRange.Value.Max;
        }

        var nitriteRange = ExtractWaterRangeFromText(ExtractParameterSegment(text, "nitrite", "nitrites", "no2"), 0m, 1000m);
        if (nitriteRange is not null)
        {
            reference.NitritesMin = nitriteRange.Value.Min;
            reference.NitritesMax = nitriteRange.Value.Max;
        }

        var ammoniaRange = ExtractWaterRangeFromText(ExtractParameterSegment(text, "ammonia", "ammoniaque", "ammonium", "nh3", "nh4"), 0m, 1000m);
        if (ammoniaRange is not null)
        {
            reference.AmmoniaMin = ammoniaRange.Value.Min;
            reference.AmmoniaMax = ammoniaRange.Value.Max;
        }
    }

    private static void PopulatePlantParametersFromText(PlantReference reference, string text)
    {
        var temperature = ExtractTemperatureRangeFromText(text, explicitTemperatureRangeOnly: true)
            ?? ExtractTemperatureRangeFromText(ExtractParameterSegment(text, "temperature", "temperatures", "temperature range", "temperature de l'eau"));
        if (temperature is not null)
        {
            reference.TemperatureMin = temperature.Value.Min;
            reference.TemperatureMax = temperature.Value.Max;
        }

        var ph = ExtractWaterRangeFromText(ExtractParameterSegment(text, "ph", "acidity", "acidite"), 0m, 14m);
        if (ph is not null)
        {
            reference.PhMin = ph.Value.Min;
            reference.PhMax = ph.Value.Max;
        }

        var hardness = ExtractHardnessRangeFromText(ExtractParameterSegment(text, "hardness", "durete", "gh", "dgh"));
        if (hardness is not null)
        {
            reference.GhMin = hardness.Value.Min;
            reference.GhMax = hardness.Value.Max;
        }

        var kh = ExtractHardnessRangeFromText(ExtractParameterSegment(text, "kh", "dkh", "carbonate hardness", "alkalinity", "alcalinite"));
        if (kh is not null)
        {
            reference.KhMin = kh.Value.Min;
            reference.KhMax = kh.Value.Max;
        }

        var volumeText = ExtractParameterSegment(text, "aquarium size", "minimum tank size", "tank size", "minimum aquarium size", "volume", "volume minimum", "aquarium minimum");
        reference.VolumeMinLiters ??= ExtractVolumeLitersFromText(volumeText);

        var nitrateRange = ExtractWaterRangeFromText(ExtractParameterSegment(text, "nitrate", "nitrates", "no3"), 0m, 1000m);
        if (nitrateRange is not null)
        {
            reference.NitratesMin = nitrateRange.Value.Min;
            reference.NitratesMax = nitrateRange.Value.Max;
        }

        var nitriteRange = ExtractWaterRangeFromText(ExtractParameterSegment(text, "nitrite", "nitrites", "no2"), 0m, 1000m);
        if (nitriteRange is not null)
        {
            reference.NitritesMin = nitriteRange.Value.Min;
            reference.NitritesMax = nitriteRange.Value.Max;
        }

        var ammoniaRange = ExtractWaterRangeFromText(ExtractParameterSegment(text, "ammonia", "ammoniaque", "ammonium", "nh3", "nh4"), 0m, 1000m);
        if (ammoniaRange is not null)
        {
            reference.AmmoniaMin = ammoniaRange.Value.Min;
            reference.AmmoniaMax = ammoniaRange.Value.Max;
        }

        reference.LightNeed = FirstNonEmpty(reference.LightNeed, ExtractPlantTextChoice(text, "lumiere", "eclairage", "light"));
        reference.Co2Need = FirstNonEmpty(reference.Co2Need, ExtractPlantTextChoice(text, "co2", "carbon dioxide"));
        reference.FertilizationNeed = FirstNonEmpty(reference.FertilizationNeed, ExtractPlantTextChoice(text, "fertilisation", "fertilization", "engrais"));
        reference.GrowthSpeed = FirstNonEmpty(reference.GrowthSpeed, ExtractPlantTextChoice(text, "croissance", "growth"));
        reference.RecommendedPlacement = FirstNonEmpty(reference.RecommendedPlacement, ExtractPlantTextChoice(text, "emplacement", "placement", "position"));
    }

    private static string ExtractPlantTextChoice(string text, params string[] labels)
    {
        var segment = ExtractParameterSegment(text, labels);
        if (string.IsNullOrWhiteSpace(segment))
        {
            return string.Empty;
        }

        var normalized = segment.ToLowerInvariant();
        if (normalized.Contains("faible") || normalized.Contains("low"))
        {
            return "Faible";
        }

        if (normalized.Contains("moyenne") || normalized.Contains("medium"))
        {
            return "Moyenne";
        }

        if (normalized.Contains("forte") || normalized.Contains("high"))
        {
            return "Forte";
        }

        if (normalized.Contains("lente") || normalized.Contains("slow"))
        {
            return "Lente";
        }

        if (normalized.Contains("rapide") || normalized.Contains("fast"))
        {
            return "Rapide";
        }

        if (normalized.Contains("avant") || normalized.Contains("foreground"))
        {
            return "Avant";
        }

        if (normalized.Contains("milieu") || normalized.Contains("midground"))
        {
            return "Milieu";
        }

        if (normalized.Contains("arriere") || normalized.Contains("background"))
        {
            return "Arriere";
        }

        return string.Empty;
    }

    private static void AssignLocalizedCommonNameFromSource(AnimalReference reference, string sourceUrl)
    {
        var commonName = CleanCommonNameTitle(reference.CommonName);
        if (!IsUsableCommonName(commonName, reference.ScientificName))
        {
            return;
        }

        reference.CommonName = commonName;
        var lowered = sourceUrl.ToLowerInvariant();
        if (lowered.Contains("fishfish.fr")
            || lowered.Contains("fishipedia.fr")
            || lowered.Contains("aquaportail.com")
            || lowered.Contains("aquachange.fr"))
        {
            reference.CommonNameFr = MergeLocalizedCommonName(reference.CommonNameFr, commonName);
            return;
        }

        if (lowered.Contains("liveaquaria.com") || lowered.Contains("seriouslyfish.com") || lowered.Contains("en.wikipedia.org"))
        {
            reference.CommonNameEn = MergeLocalizedCommonName(reference.CommonNameEn, commonName);
        }
    }

    private static void AssignLocalizedPlantCommonNameFromSource(PlantReference reference, string sourceUrl)
    {
        var commonName = CleanCommonNameTitle(reference.CommonName);
        if (!IsUsableCommonName(commonName, reference.ScientificName))
        {
            return;
        }

        reference.CommonName = commonName;
        var lowered = sourceUrl.ToLowerInvariant();
        if (lowered.Contains("fishipedia.fr")
            || lowered.Contains("aquaplante.fr")
            || lowered.Contains("aquabiance.fr")
            || lowered.Contains("aquarilis.fr"))
        {
            reference.CommonNameFr = MergeLocalizedCommonName(reference.CommonNameFr, commonName);
            return;
        }

        if (lowered.Contains("tropica.com") || lowered.Contains("en.wikipedia.org"))
        {
            reference.CommonNameEn = MergeLocalizedCommonName(reference.CommonNameEn, commonName);
        }
    }

    private static async Task EnrichPlantCommonNamesFromWikipediaAsync(
        HttpClient httpClient,
        PlantReference reference,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeScientificName(reference.ScientificName, out var scientificName))
        {
            return;
        }

        var encodedTitle = Uri.EscapeDataString(scientificName.Replace(' ', '_'));
        var apiUrl = $"https://en.wikipedia.org/w/api.php?action=query&redirects=1&prop=langlinks&lllimit=max&titles={encodedTitle}&format=json";
        var json = await TryGetStringWithRetryAsync(httpClient, apiUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("query", out var queryElement)
                || !queryElement.TryGetProperty("pages", out var pagesElement)
                || pagesElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var page in pagesElement.EnumerateObject())
            {
                var pageElement = page.Value;
                if (pageElement.TryGetProperty("missing", out _))
                {
                    continue;
                }

                if (pageElement.TryGetProperty("title", out var titleElement))
                {
                    AssignLocalizedPlantCommonName(reference, "en", titleElement.GetString());
                }

                if (!pageElement.TryGetProperty("langlinks", out var langlinksElement)
                    || langlinksElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var langlink in langlinksElement.EnumerateArray())
                {
                    if (!langlink.TryGetProperty("lang", out var langElement)
                        || !langlink.TryGetProperty("*", out var localizedTitleElement))
                    {
                        continue;
                    }

                    var languageCode = langElement.GetString();
                    if (languageCode is "fr" or "en" or "de")
                    {
                        AssignLocalizedPlantCommonName(reference, languageCode, localizedTitleElement.GetString());
                    }
                }
            }

            if (!IsUsableCommonName(reference.CommonName, reference.ScientificName))
            {
                reference.CommonName = FirstNonEmpty(reference.CommonNameFr, reference.CommonNameEn, reference.CommonNameDe, scientificName);
            }
        }
        catch
        {
            // Ignore malformed Wikipedia payloads; the import can continue with the source names already found.
        }
    }

    private static void AssignLocalizedPlantCommonName(PlantReference reference, string languageCode, string? rawName)
    {
        var commonName = CleanCommonNameTitle(rawName);
        if (!IsUsableCommonName(commonName, reference.ScientificName))
        {
            return;
        }

        switch (languageCode)
        {
            case "fr":
                reference.CommonNameFr = MergeLocalizedCommonName(reference.CommonNameFr, commonName);
                break;
            case "en":
                reference.CommonNameEn = MergeLocalizedCommonName(reference.CommonNameEn, commonName);
                break;
            case "de":
                reference.CommonNameDe = MergeLocalizedCommonName(reference.CommonNameDe, commonName);
                break;
        }
    }

    private static async Task EnrichAnimalCommonNamesFromWikipediaAsync(
        HttpClient httpClient,
        AnimalReference reference,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeScientificName(reference.ScientificName, out var scientificName))
        {
            return;
        }

        var encodedTitle = Uri.EscapeDataString(scientificName.Replace(' ', '_'));
        var apiUrl = $"https://en.wikipedia.org/w/api.php?action=query&redirects=1&prop=langlinks&lllimit=max&titles={encodedTitle}&format=json";
        var json = await TryGetStringWithRetryAsync(httpClient, apiUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("query", out var queryElement)
                || !queryElement.TryGetProperty("pages", out var pagesElement)
                || pagesElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var page in pagesElement.EnumerateObject())
            {
                var pageElement = page.Value;
                if (pageElement.TryGetProperty("missing", out _))
                {
                    continue;
                }

                if (pageElement.TryGetProperty("title", out var titleElement))
                {
                    AssignLocalizedCommonName(reference, "en", titleElement.GetString());
                }

                if (!pageElement.TryGetProperty("langlinks", out var langlinksElement)
                    || langlinksElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var langlink in langlinksElement.EnumerateArray())
                {
                    if (!langlink.TryGetProperty("lang", out var langElement)
                        || !langlink.TryGetProperty("*", out var localizedTitleElement))
                    {
                        continue;
                    }

                    var languageCode = langElement.GetString();
                    if (languageCode is "fr" or "en" or "de")
                    {
                        AssignLocalizedCommonName(reference, languageCode, localizedTitleElement.GetString());
                    }
                }
            }

            if (!IsUsableCommonName(reference.CommonName, reference.ScientificName))
            {
                reference.CommonName = FirstNonEmpty(reference.CommonNameFr, reference.CommonNameEn, reference.CommonNameDe, scientificName);
            }
        }
        catch
        {
            // Ignore malformed Wikipedia payloads; the import can continue with the source names already found.
        }
    }

    private static void AssignLocalizedCommonName(AnimalReference reference, string languageCode, string? rawName)
    {
        var commonName = CleanCommonNameTitle(rawName);
        if (!IsUsableCommonName(commonName, reference.ScientificName))
        {
            return;
        }

        switch (languageCode)
        {
            case "fr":
                reference.CommonNameFr = MergeLocalizedCommonName(reference.CommonNameFr, commonName);
                break;
            case "en":
                reference.CommonNameEn = MergeLocalizedCommonName(reference.CommonNameEn, commonName);
                break;
            case "de":
                reference.CommonNameDe = MergeLocalizedCommonName(reference.CommonNameDe, commonName);
                break;
        }
    }

    private static string MergeLocalizedCommonName(string existingName, string candidateName)
    {
        return string.IsNullOrWhiteSpace(existingName) ? TrimToMax(candidateName, 160) : existingName;
    }

    private static bool IsUsableCommonName(string? commonName, string scientificName)
    {
        if (string.IsNullOrWhiteSpace(commonName))
        {
            return false;
        }

        if (string.Equals(commonName, scientificName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryNormalizeScientificName(commonName, out var normalizedScientificName)
            && string.Equals(normalizedScientificName, scientificName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string CleanCommonNameTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = WebUtility.HtmlDecode(value)
            .Replace('_', ' ')
            .Trim();
        cleaned = Regex.Replace(cleaned, @"\s*\([^)]*\)\s*$", string.Empty).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return TrimToMax(cleaned, 160);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static IEnumerable<string> ExtractLinks(string html, string hrefPattern, string baseUrl)
    {
        return Regex.Matches(html, hrefPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Cast<Match>()
            .Select(match => match.Groups["href"].Value)
            .Where(href => !string.IsNullOrWhiteSpace(href))
            .Select(href => href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : $"{baseUrl.TrimEnd('/')}/{href.TrimStart('/')}")
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? ExtractScientificNameFromProfileHtml(string html)
    {
        foreach (Match match in Regex.Matches(html, @"<(?:i|em)\b[^>]*>(.*?)</(?:i|em)>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var candidate = ExtractScientificNameFromText(CleanHtmlText(match.Groups[1].Value));
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ExtractScientificNameFromPageHeading(string? heading)
    {
        if (string.IsNullOrWhiteSpace(heading))
        {
            return null;
        }

        var match = Regex.Match(heading, @"^\s*(?:poisson|fish)\s+(?<genus>[a-z]{3,})\s+(?<species>[a-z][a-z\-]{2,})\b", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var genus = char.ToUpperInvariant(match.Groups["genus"].Value[0]) + match.Groups["genus"].Value[1..].ToLowerInvariant();
        var species = match.Groups["species"].Value.ToLowerInvariant();
        return LooksLikeScientificName(genus, species) ? NormalizeScientificName(genus, species) : null;
    }

    private static string? ExtractScientificNameFromLabeledProfileText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(
            text,
            @"(?:nom scientifique|scientific name|espece|espèce)\s*:?\s*(?<genus>[A-Z][a-z]{2,})\s+(?<species>[a-z][a-z\-]{2,})\b",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var genus = char.ToUpperInvariant(match.Groups["genus"].Value[0]) + match.Groups["genus"].Value[1..].ToLowerInvariant();
        var species = match.Groups["species"].Value.ToLowerInvariant();
        return LooksLikeScientificName(genus, species) ? NormalizeScientificName(genus, species) : null;
    }

    private static string? ExtractScientificNameFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (Match match in Regex.Matches(text, @"\b([A-Z][a-z]{2,})\s([a-z][a-z\-]{2,})\b"))
        {
            var genus = match.Groups[1].Value;
            var species = match.Groups[2].Value;
            if (LooksLikeScientificName(genus, species))
            {
                return NormalizeScientificName(genus, species);
            }
        }

        return null;
    }

    private static string? ExtractScientificNameFromSlug(string url)
    {
        var lastSegment = new Uri(url).Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
        var match = Regex.Match(lastSegment, @"^([a-z]{3,})-([a-z][a-z\-]{2,})$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var genus = char.ToUpperInvariant(match.Groups[1].Value[0]) + match.Groups[1].Value[1..].ToLowerInvariant();
        var species = match.Groups[2].Value.ToLowerInvariant();
        return LooksLikeScientificName(genus, species) ? NormalizeScientificName(genus, species) : null;
    }

    private static string? ExtractPageHeading(string html)
    {
        var match = Regex.Match(html, @"<h1\b[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            match = Regex.Match(html, @"<title\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        if (!match.Success)
        {
            return null;
        }

        var heading = CleanHtmlText(match.Groups[1].Value);
        heading = Regex.Replace(heading, @"\s*[-|].*$", string.Empty).Trim();
        return string.IsNullOrWhiteSpace(heading) ? null : TrimToMax(heading, 160);
    }

    private static string ExtractCommonNameFromProfilePage(string url, string? pageHeading, string normalizedScientific)
    {
        var loweredUrl = url.ToLowerInvariant();
        if (loweredUrl.Contains("fishfish.fr") && !string.IsNullOrWhiteSpace(pageHeading))
        {
            var parenthesizedCommonName = Regex.Match(
                pageHeading,
                @"^\s*Poisson\s+[a-z]{3,}\s+[a-z][a-z\-]{2,}\s*\((?<common>[^)]+)\)",
                RegexOptions.IgnoreCase);
            if (parenthesizedCommonName.Success)
            {
                return CleanCommonNameTitle(parenthesizedCommonName.Groups["common"].Value);
            }

            var withoutFishPrefix = Regex.Replace(pageHeading, @"^\s*Poisson\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (!string.Equals(withoutFishPrefix, pageHeading, StringComparison.Ordinal)
                && TryNormalizeScientificName(withoutFishPrefix, out var headingScientific)
                && string.Equals(headingScientific, normalizedScientific, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedScientific;
            }
        }

        var cleanedHeading = CleanCommonNameTitle(pageHeading);
        return string.IsNullOrWhiteSpace(cleanedHeading) ? normalizedScientific : cleanedHeading;
    }

    private static bool TryNormalizeScientificName(string? scientificName, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(scientificName))
        {
            return false;
        }

        var match = Regex.Match(scientificName.Trim(), @"^\s*([A-Z][a-z]{2,})\s([a-z][a-z\-]{2,})\s*$");
        if (!match.Success)
        {
            return false;
        }

        var genus = match.Groups[1].Value;
        var species = match.Groups[2].Value;
        if (!LooksLikeScientificName(genus, species))
        {
            return false;
        }

        normalized = NormalizeScientificName(genus, species);
        return true;
    }

    private static async Task<bool> TryEnrichAnimalReferenceFromSeriouslyFishAsync(
        HttpClient httpClient,
        AnimalReference reference,
        CancellationToken cancellationToken)
    {
        var slug = BuildSeriouslyFishSlug(reference.ScientificName);
        if (string.IsNullOrWhiteSpace(slug))
        {
            return false;
        }

        var url = $"https://www.seriouslyfish.com/species/{slug}/";
        var html = await TryGetStringWithRetryAsync(httpClient, url, cancellationToken);
        if (string.IsNullOrWhiteSpace(html) || !html.Contains("Water Conditions", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var text = CleanHtmlText(html);
        var changed = false;

        var aquariumSize = ExtractBestSegment(text, "Aquarium Size", "Maintenance", "Water Conditions", "Diet", "Behaviour");
        var volume = ExtractVolumeLitersFromText(aquariumSize);
        if (volume.HasValue)
        {
            reference.VolumeMinLiters = volume.Value;
            changed = true;
        }

        var temperatureText = ExtractBestSegment(text, "Temperature:", "pH:", "Hardness:", "Click here", "Diet");
        var temperature = ExtractWaterRangeFromText(temperatureText, 0m, 40m);
        if (temperature is not null)
        {
            reference.TemperatureMin = temperature.Value.Min;
            reference.TemperatureMax = temperature.Value.Max;
            changed = true;
        }

        var phText = ExtractBestSegment(text, "pH:", "Hardness:", "Click here", "Diet");
        var ph = ExtractWaterRangeFromText(phText, 0m, 14m);
        if (ph is not null)
        {
            reference.PhMin = ph.Value.Min;
            reference.PhMax = ph.Value.Max;
            changed = true;
        }

        var hardnessText = ExtractBestSegment(text, "Hardness:", "Click here", "Diet", "Behaviour");
        var hardness = ExtractHardnessRangeFromText(hardnessText);
        if (hardness is not null)
        {
            reference.GhMin = hardness.Value.Min;
            reference.GhMax = hardness.Value.Max;
            changed = true;
        }

        if (changed)
        {
            reference.SourceUrl = url;
        }

        return changed;
    }

    private static string? BuildSeriouslyFishSlug(string scientificName)
    {
        var match = Regex.Match(scientificName, @"^\s*([A-Z][a-z]+)\s+([a-z][a-z\-]+)\s*$");
        return match.Success
            ? $"{match.Groups[1].Value.ToLowerInvariant()}-{match.Groups[2].Value.ToLowerInvariant()}"
            : null;
    }

    private static int CountEssentialPlantParameters(PlantReference reference)
    {
        var count = 0;
        if (reference.PhMin.HasValue || reference.PhMax.HasValue)
        {
            count++;
        }

        if (reference.GhMin.HasValue || reference.GhMax.HasValue)
        {
            count++;
        }

        if (reference.KhMin.HasValue || reference.KhMax.HasValue)
        {
            count++;
        }

        if (reference.TemperatureMin.HasValue || reference.TemperatureMax.HasValue)
        {
            count++;
        }

        if (reference.AmmoniaMin.HasValue || reference.AmmoniaMax.HasValue)
        {
            count++;
        }

        if (reference.NitritesMin.HasValue || reference.NitritesMax.HasValue)
        {
            count++;
        }

        if (reference.NitratesMin.HasValue || reference.NitratesMax.HasValue)
        {
            count++;
        }

        if (reference.VolumeMinLiters.HasValue)
        {
            count++;
        }

        return count;
    }

    private static string BuildPlantCandidateRejectionReason(PlantReference reference, int minimumParameterGroupCount)
    {
        var score = CountEssentialPlantParameters(reference);
        var missing = GetMissingPlantParameterGroups(reference).ToList();
        var missingText = missing.Count == 0
            ? "aucun groupe manquant identifie"
            : $"manquants: {string.Join(", ", missing)}";

        return TrimToMax($"Seulement {score}/{minimumParameterGroupCount} groupes de parametres renseignes; {missingText}.", 220);
    }

    private static IEnumerable<string> GetMissingPlantParameterGroups(PlantReference reference)
    {
        if (!reference.PhMin.HasValue && !reference.PhMax.HasValue)
        {
            yield return "pH";
        }

        if (!reference.GhMin.HasValue && !reference.GhMax.HasValue)
        {
            yield return "GH";
        }

        if (!reference.KhMin.HasValue && !reference.KhMax.HasValue)
        {
            yield return "KH";
        }

        if (!reference.TemperatureMin.HasValue && !reference.TemperatureMax.HasValue)
        {
            yield return "Temperature";
        }

        if (!reference.AmmoniaMin.HasValue && !reference.AmmoniaMax.HasValue)
        {
            yield return "Amoniac";
        }

        if (!reference.NitritesMin.HasValue && !reference.NitritesMax.HasValue)
        {
            yield return "Nitrites";
        }

        if (!reference.NitratesMin.HasValue && !reference.NitratesMax.HasValue)
        {
            yield return "Nitrates";
        }

        if (!reference.VolumeMinLiters.HasValue)
        {
            yield return "Volume minimum";
        }
    }

    private static int CountEssentialAnimalParameters(AnimalReference reference)
    {
        var count = 0;
        if (reference.PhMin.HasValue || reference.PhMax.HasValue)
        {
            count++;
        }

        if (reference.GhMin.HasValue || reference.GhMax.HasValue)
        {
            count++;
        }

        if (reference.KhMin.HasValue || reference.KhMax.HasValue)
        {
            count++;
        }

        if (reference.TemperatureMin.HasValue || reference.TemperatureMax.HasValue)
        {
            count++;
        }

        if (reference.AmmoniaMin.HasValue || reference.AmmoniaMax.HasValue)
        {
            count++;
        }

        if (reference.NitritesMin.HasValue || reference.NitritesMax.HasValue)
        {
            count++;
        }

        if (reference.NitratesMin.HasValue || reference.NitratesMax.HasValue)
        {
            count++;
        }

        if (reference.VolumeMinLiters.HasValue)
        {
            count++;
        }

        return count;
    }

    private static string BuildAnimalCandidateRejectionReason(AnimalReference reference, int minimumParameterGroupCount)
    {
        var score = CountEssentialAnimalParameters(reference);
        var missing = GetMissingAnimalParameterGroups(reference).ToList();
        var missingText = missing.Count == 0
            ? "aucun groupe manquant identifie"
            : $"manquants: {string.Join(", ", missing)}";

        return TrimToMax($"Seulement {score}/{minimumParameterGroupCount} groupes de parametres renseignes; {missingText}.", 220);
    }

    private static IEnumerable<string> GetMissingAnimalParameterGroups(AnimalReference reference)
    {
        if (!reference.PhMin.HasValue && !reference.PhMax.HasValue)
        {
            yield return "pH";
        }

        if (!reference.GhMin.HasValue && !reference.GhMax.HasValue)
        {
            yield return "GH";
        }

        if (!reference.KhMin.HasValue && !reference.KhMax.HasValue)
        {
            yield return "KH";
        }

        if (!reference.TemperatureMin.HasValue && !reference.TemperatureMax.HasValue)
        {
            yield return "Temperature";
        }

        if (!reference.AmmoniaMin.HasValue && !reference.AmmoniaMax.HasValue)
        {
            yield return "Amoniac";
        }

        if (!reference.NitritesMin.HasValue && !reference.NitritesMax.HasValue)
        {
            yield return "Nitrites";
        }

        if (!reference.NitratesMin.HasValue && !reference.NitratesMax.HasValue)
        {
            yield return "Nitrates";
        }

        if (!reference.VolumeMinLiters.HasValue)
        {
            yield return "Volume minimum";
        }
    }

    private static string CleanHtmlText(string html)
    {
        var withoutScripts = Regex.Replace(html, @"<(script|style|sup)\b[^>]*>.*?</\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var withoutTags = Regex.Replace(withoutScripts, @"<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(withoutTags).Replace('\u00A0', ' ');
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string? ExtractScientificNameFromHtmlCell(string html)
    {
        var text = CleanHtmlText(html);
        var match = Regex.Match(text, @"\b([A-Z][a-z]{2,})\s([a-z][a-z\-]{2,})\b");
        if (!match.Success)
        {
            return null;
        }

        var genus = match.Groups[1].Value;
        var species = match.Groups[2].Value;
        return LooksLikeScientificName(genus, species) ? NormalizeScientificName(genus, species) : null;
    }

    private static string ExtractBestSegment(string text, string label, params string[] stopLabels)
    {
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(label, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return string.Empty;
            }

            var segmentStart = index + label.Length;
            var segmentEnd = text.Length;
            foreach (var stopLabel in stopLabels)
            {
                var stop = text.IndexOf(stopLabel, segmentStart, StringComparison.OrdinalIgnoreCase);
                if (stop >= 0 && stop < segmentEnd)
                {
                    segmentEnd = stop;
                }
            }

            var segment = text[segmentStart..Math.Min(segmentEnd, segmentStart + 600)].Trim();
            if (Regex.IsMatch(segment, @"\d"))
            {
                return segment;
            }

            start = segmentStart;
        }

        return string.Empty;
    }

    private static string ExtractParameterSegment(string text, params string[] labels)
    {
        var normalizedText = NormalizeForSearch(text);
        var normalizedLabels = labels.Select(NormalizeForSearch).Where(label => !string.IsNullOrWhiteSpace(label)).ToList();
        var stopLabels = new[]
        {
            "temperature", "ph", "acidity", "acidite", "hardness", "durete", "gh", "dgh", "kh",
            "carbonate hardness", "alkalinity", "alcalinite", "volume", "aquarium size",
            "minimum tank size", "tank size", "nitrate", "nitrates", "nitrite", "nitrites",
            "ammonia", "ammoniaque", "ammonium", "no2", "no3", "nh3", "nh4", "diet",
            "behaviour", "behavior", "maintenance", "compatibility", "origin"
        };

        foreach (var label in normalizedLabels)
        {
            var start = 0;
            while (start < normalizedText.Length)
            {
                var labelIndex = normalizedText.IndexOf(label, start, StringComparison.OrdinalIgnoreCase);
                if (labelIndex < 0)
                {
                    break;
                }

                if (!IsSearchLabelMatch(normalizedText, labelIndex, label))
                {
                    start = labelIndex + Math.Max(1, label.Length);
                    continue;
                }

                var segmentStart = Math.Min(text.Length, labelIndex + label.Length);
                var segmentEnd = Math.Min(text.Length, segmentStart + 260);
                foreach (var stopLabel in stopLabels)
                {
                    var normalizedStop = NormalizeForSearch(stopLabel);
                    if (string.Equals(normalizedStop, label, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var stopIndex = normalizedText.IndexOf(normalizedStop, segmentStart, StringComparison.OrdinalIgnoreCase);
                    if (stopIndex > segmentStart && stopIndex < segmentEnd)
                    {
                        segmentEnd = stopIndex;
                    }
                }

                var segment = text[segmentStart..segmentEnd].Trim();
                if (Regex.IsMatch(segment, @"\d"))
                {
                    return segment;
                }

                start = labelIndex + label.Length;
            }
        }

        return string.Empty;
    }

    private static bool IsSearchLabelMatch(string text, int index, string label)
    {
        if (label.Length > 3 || label.Contains(' '))
        {
            return true;
        }

        var before = index == 0 ? ' ' : text[index - 1];
        var afterIndex = index + label.Length;
        var after = afterIndex >= text.Length ? ' ' : text[afterIndex];
        return !char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after);
    }

    private static string NormalizeForSearch(string text)
    {
        var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
        var chars = normalized
            .Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(ch => ch == '\u00A0' ? ' ' : char.ToLowerInvariant(ch))
            .ToArray();
        return new string(chars).Normalize(System.Text.NormalizationForm.FormC);
    }

    private static (decimal Min, decimal Max)? ExtractTemperatureRangeFromText(string text, bool explicitTemperatureRangeOnly = false)
    {
        var explicitRange = ExtractExplicitCelsiusRangeFromText(text);
        if (explicitRange is not null)
        {
            return explicitRange;
        }

        if (explicitTemperatureRangeOnly)
        {
            return null;
        }

        var celsiusValues = ExtractDecimalValues(text)
            .Where(value => value >= 0m && value <= 40m)
            .ToList();
        if (celsiusValues.Count > 0)
        {
            return celsiusValues.Count == 1
                ? (celsiusValues[0], celsiusValues[0])
                : (celsiusValues.Min(), celsiusValues.Max());
        }

        var fahrenheitValues = ExtractDecimalValues(text)
            .Where(value => value >= 45m && value <= 110m)
            .Select(value => Math.Round((value - 32m) * 5m / 9m, 1))
            .Where(value => value >= 0m && value <= 40m)
            .ToList();
        if (fahrenheitValues.Count == 0)
        {
            return null;
        }

        return fahrenheitValues.Count == 1
            ? (fahrenheitValues[0], fahrenheitValues[0])
            : (fahrenheitValues.Min(), fahrenheitValues.Max());
    }

    private static (decimal Min, decimal Max)? ExtractExplicitCelsiusRangeFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = WebUtility.HtmlDecode(text)
            .Replace('\u00A0', ' ')
            .Replace('–', '-')
            .Replace('—', '-');
        var matches = Regex.Matches(
            normalized,
            @"(?<min>\d+(?:[.,]\d+)?)\s*(?:°\s*[Cc])?\s*(?:-|a|à|et|to|and)\s*(?<max>\d+(?:[.,]\d+)?)\s*°?\s*[Cc]\b",
            RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            if (!TryParseInvariantDecimal(match.Groups["min"].Value, out var min)
                || !TryParseInvariantDecimal(match.Groups["max"].Value, out var max)
                || min < 0m
                || max < 0m
                || min > 40m
                || max > 40m)
            {
                continue;
            }

            return min <= max ? (min, max) : (max, min);
        }

        return null;
    }

    private static (decimal Min, decimal Max)? ExtractWaterRangeFromText(string text, decimal minAllowed, decimal maxAllowed)
    {
        var values = ExtractDecimalValues(text)
            .Where(value => value >= minAllowed && value <= maxAllowed)
            .ToList();
        if (values.Count == 0)
        {
            return null;
        }

        return values.Count == 1
            ? (values[0], values[0])
            : (values.Min(), values.Max());
    }

    private static (decimal Min, decimal Max)? ExtractHardnessRangeFromText(string text)
    {
        var values = ExtractDecimalValues(text).ToList();
        if (values.Count == 0)
        {
            return null;
        }

        if (text.Contains("ppm", StringComparison.OrdinalIgnoreCase))
        {
            var converted = values
                .Select(value => Math.Round(value / 17.9m, 1))
                .Where(value => value >= 0m && value <= 40m)
                .ToList();
            if (converted.Count > 0)
            {
                return converted.Count == 1
                    ? (converted[0], converted[0])
                    : (converted.Min(), converted.Max());
            }
        }

        var degreeValues = values
            .Where(value => value >= 0m && value <= 40m)
            .ToList();
        if (degreeValues.Count == 0)
        {
            return null;
        }

        return degreeValues.Count == 1
            ? (degreeValues[0], degreeValues[0])
            : (degreeValues.Min(), degreeValues.Max());
    }

    private static int? ExtractVolumeLitersFromText(string text)
    {
        var literMatch = Regex.Match(text, @"(\d+(?:[.,]\d+)?)\s*(?:l|litre|litres|liter|liters)\b", RegexOptions.IgnoreCase);
        if (literMatch.Success && TryParseInvariantDecimal(literMatch.Groups[1].Value, out var liters))
        {
            return (int)Math.Round(liters);
        }

        var gallonMatch = Regex.Match(text, @"(\d+(?:[.,]\d+)?)\s*(?:gal|gallon|gallons)\b", RegexOptions.IgnoreCase);
        if (gallonMatch.Success && TryParseInvariantDecimal(gallonMatch.Groups[1].Value, out var gallons))
        {
            return (int)Math.Round(gallons * 3.78541m);
        }

        return null;
    }

    private static IEnumerable<decimal> ExtractDecimalValues(string text)
    {
        return Regex.Matches(text.Replace('–', '-').Replace('—', '-'), @"\d+(?:[.,]\d+)?")
            .Cast<Match>()
            .Select(match => TryParseInvariantDecimal(match.Value, out var parsed) ? parsed : (decimal?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value);
    }

    private static bool TryParseInvariantDecimal(string text, out decimal value)
    {
        return decimal.TryParse(
            text.Replace(',', '.'),
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }

    private static void SanitizePlantReferenceForStorage(PlantReference reference)
    {
        reference.CommonName = CapitalizeFirstLetter(reference.CommonName, 160);
        reference.CommonNameFr = CapitalizeFirstLetter(reference.CommonNameFr, 160);
        reference.CommonNameEn = CapitalizeFirstLetter(reference.CommonNameEn, 160);
        reference.CommonNameDe = CapitalizeFirstLetter(reference.CommonNameDe, 160);
        reference.ScientificName = TrimToMax(reference.ScientificName, 180);
        reference.LightNeed = TrimToMax(reference.LightNeed, 80);
        reference.Co2Need = TrimToMax(reference.Co2Need, 80);
        reference.FertilizationNeed = TrimToMax(reference.FertilizationNeed, 120);
        reference.GrowthSpeed = TrimToMax(reference.GrowthSpeed, 80);
        reference.RecommendedPlacement = TrimToMax(reference.RecommendedPlacement, 120);
        reference.Behavior = TrimToMax(reference.Behavior, 180);
        reference.Compatibility = TrimToMax(reference.Compatibility, 220);
        reference.SourceUrl = TrimToMax(reference.SourceUrl, 512);
        reference.PhMin = SanitizeBound(reference.PhMin, 0m, 14m);
        reference.PhMax = SanitizeBound(reference.PhMax, 0m, 14m);
        reference.GhMin = SanitizeBound(reference.GhMin, 0m, 40m);
        reference.GhMax = SanitizeBound(reference.GhMax, 0m, 40m);
        reference.KhMin = SanitizeBound(reference.KhMin, 0m, 40m);
        reference.KhMax = SanitizeBound(reference.KhMax, 0m, 40m);
        reference.TemperatureMin = SanitizeBound(reference.TemperatureMin, 0m, 40m);
        reference.TemperatureMax = SanitizeBound(reference.TemperatureMax, 0m, 40m);
        reference.AmmoniaMin = SanitizeBound(reference.AmmoniaMin, 0m, 1000m);
        reference.AmmoniaMax = SanitizeBound(reference.AmmoniaMax, 0m, 1000m);
        reference.NitritesMin = SanitizeBound(reference.NitritesMin, 0m, 1000m);
        reference.NitritesMax = SanitizeBound(reference.NitritesMax, 0m, 1000m);
        reference.NitratesMin = SanitizeBound(reference.NitratesMin, 0m, 1000m);
        reference.NitratesMax = SanitizeBound(reference.NitratesMax, 0m, 1000m);
        (reference.PhMin, reference.PhMax) = NormalizeNullableRange(reference.PhMin, reference.PhMax);
        (reference.GhMin, reference.GhMax) = NormalizeNullableRange(reference.GhMin, reference.GhMax);
        (reference.KhMin, reference.KhMax) = NormalizeNullableRange(reference.KhMin, reference.KhMax);
        (reference.TemperatureMin, reference.TemperatureMax) = NormalizeNullableRange(reference.TemperatureMin, reference.TemperatureMax);
        (reference.AmmoniaMin, reference.AmmoniaMax) = NormalizeNullableRange(reference.AmmoniaMin, reference.AmmoniaMax);
        (reference.NitritesMin, reference.NitritesMax) = NormalizeNullableRange(reference.NitritesMin, reference.NitritesMax);
        (reference.NitratesMin, reference.NitratesMax) = NormalizeNullableRange(reference.NitratesMin, reference.NitratesMax);
        if (reference.VolumeMinLiters is < 0 or > 100000)
        {
            reference.VolumeMinLiters = null;
        }
    }

    private static void SanitizeAnimalReferenceForStorage(AnimalReference reference)
    {
        reference.CommonName = CapitalizeFirstLetter(reference.CommonName, 160);
        reference.CommonNameFr = CapitalizeFirstLetter(reference.CommonNameFr, 160);
        reference.CommonNameEn = CapitalizeFirstLetter(reference.CommonNameEn, 160);
        reference.CommonNameDe = CapitalizeFirstLetter(reference.CommonNameDe, 160);
        reference.ScientificName = TrimToMax(reference.ScientificName, 180);
        reference.Behavior = TrimToMax(reference.Behavior, 180);
        reference.Compatibility = TrimToMax(reference.Compatibility, 220);
        reference.SourceUrl = TrimToMax(reference.SourceUrl, 512);
        reference.PhMin = SanitizeBound(reference.PhMin, 0m, 14m);
        reference.PhMax = SanitizeBound(reference.PhMax, 0m, 14m);
        reference.GhMin = SanitizeBound(reference.GhMin, 0m, 40m);
        reference.GhMax = SanitizeBound(reference.GhMax, 0m, 40m);
        reference.KhMin = SanitizeBound(reference.KhMin, 0m, 40m);
        reference.KhMax = SanitizeBound(reference.KhMax, 0m, 40m);
        reference.TemperatureMin = SanitizeBound(reference.TemperatureMin, 0m, 40m);
        reference.TemperatureMax = SanitizeBound(reference.TemperatureMax, 0m, 40m);
        reference.AmmoniaMin = SanitizeBound(reference.AmmoniaMin, 0m, 1000m);
        reference.AmmoniaMax = SanitizeBound(reference.AmmoniaMax, 0m, 1000m);
        reference.NitritesMin = SanitizeBound(reference.NitritesMin, 0m, 1000m);
        reference.NitritesMax = SanitizeBound(reference.NitritesMax, 0m, 1000m);
        reference.NitratesMin = SanitizeBound(reference.NitratesMin, 0m, 1000m);
        reference.NitratesMax = SanitizeBound(reference.NitratesMax, 0m, 1000m);
        (reference.PhMin, reference.PhMax) = NormalizeNullableRange(reference.PhMin, reference.PhMax);
        (reference.GhMin, reference.GhMax) = NormalizeNullableRange(reference.GhMin, reference.GhMax);
        (reference.KhMin, reference.KhMax) = NormalizeNullableRange(reference.KhMin, reference.KhMax);
        (reference.TemperatureMin, reference.TemperatureMax) = NormalizeNullableRange(reference.TemperatureMin, reference.TemperatureMax);
        (reference.AmmoniaMin, reference.AmmoniaMax) = NormalizeNullableRange(reference.AmmoniaMin, reference.AmmoniaMax);
        (reference.NitritesMin, reference.NitritesMax) = NormalizeNullableRange(reference.NitritesMin, reference.NitritesMax);
        (reference.NitratesMin, reference.NitratesMax) = NormalizeNullableRange(reference.NitratesMin, reference.NitratesMax);
        if (reference.VolumeMinLiters is < 0 or > 100000)
        {
            reference.VolumeMinLiters = null;
        }
    }

    private static (decimal? Min, decimal? Max) NormalizeNullableRange(decimal? min, decimal? max)
    {
        return min.HasValue && max.HasValue && max.Value < min.Value
            ? (max, min)
            : (min, max);
    }

    private static string CapitalizeFirstLetter(string? value, int maxLength)
    {
        var text = TrimToMax(value, maxLength);
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var chars = text.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (!char.IsLetter(chars[index]))
            {
                continue;
            }

            chars[index] = char.ToUpper(chars[index], CultureInfo.CurrentCulture);
            break;
        }

        return new string(chars);
    }

    private static string? CapitalizeFirstLetterOrNull(string? value, int maxLength)
    {
        var text = CapitalizeFirstLetter(value, maxLength);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static decimal? SanitizeBound(decimal? value, decimal min, decimal max)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value < min || value.Value > max ? null : value.Value;
    }

    private static async Task<string?> TryFetchWikipediaWikitextAsync(
        HttpClient httpClient,
        string pageTitle,
        CancellationToken cancellationToken)
    {
        var encodedTitle = Uri.EscapeDataString(pageTitle.Replace(' ', '_'));
        var apiUrl = $"https://en.wikipedia.org/w/api.php?action=parse&page={encodedTitle}&prop=wikitext&format=json";
        var json = await TryGetStringWithRetryAsync(httpClient, apiUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("parse", out var parseElement)
                && parseElement.TryGetProperty("wikitext", out var wikiTextElement)
                && wikiTextElement.TryGetProperty("*", out var textElement))
            {
                return textElement.GetString();
            }
        }
        catch
        {
            // Ignore malformed Wikipedia payloads.
        }

        return null;
    }

    private static async Task EnrichAnimalReferenceFromWikipediaAsync(
        HttpClient httpClient,
        string pageTitle,
        AnimalReference reference,
        CancellationToken cancellationToken)
    {
        var encodedTitle = Uri.EscapeDataString(pageTitle.Replace(' ', '_'));
        var apiUrl = $"https://en.wikipedia.org/w/api.php?action=parse&page={encodedTitle}&prop=wikitext&format=json";
        var json = await TryGetStringWithRetryAsync(httpClient, apiUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("parse", out var parseElement)
                || !parseElement.TryGetProperty("wikitext", out var wikiTextElement)
                || !wikiTextElement.TryGetProperty("*", out var textElement))
            {
                return;
            }

            var wikiText = textElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(wikiText))
            {
                return;
            }

            var tempRange = ExtractRangeForKeys(wikiText, "temperature", "temp", "temps");
            if (tempRange is not null)
            {
                reference.TemperatureMin = tempRange.Value.Min;
                reference.TemperatureMax = tempRange.Value.Max;
            }

            var phRange = ExtractRangeForKeys(wikiText, "ph");
            if (phRange is not null)
            {
                reference.PhMin = phRange.Value.Min;
                reference.PhMax = phRange.Value.Max;
            }

            var hardnessRange = ExtractRangeForKeys(wikiText, "hardness", "gh", "dgh");
            if (hardnessRange is not null)
            {
                reference.GhMin = hardnessRange.Value.Min;
                reference.GhMax = hardnessRange.Value.Max;
            }
        }
        catch
        {
            // Ignore malformed species page payloads.
        }
    }

    private static (decimal Min, decimal Max)? ExtractRangeForKeys(string wikiText, params string[] keys)
    {
        var lines = wikiText.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('|'))
            {
                continue;
            }

            var lowered = line.ToLowerInvariant();
            if (!keys.Any(key => lowered.Contains(key)))
            {
                continue;
            }

            var values = Regex.Matches(line, @"\d+(?:[.,]\d+)?")
                .Select(match => match.Value.Replace(',', '.'))
                .Select(value => decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : (decimal?)null)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();

            if (values.Count == 0)
            {
                continue;
            }

            if (values.Count == 1)
            {
                return (values[0], values[0]);
            }

            var min = values.Min();
            var max = values.Max();
            return (min, max);
        }

        return null;
    }

    private static bool IsSubspeciesToken(string token)
    {
        var lowered = token.ToLowerInvariant();
        return lowered is "sp." or "cf." or "aff." or "ssp." or "subsp.";
    }

    private static AnimalReferenceEnvironment InferAnimalEnvironment(
        string url,
        string html,
        string scientificName,
        AnimalReferenceEnvironment defaultEnvironment)
    {
        var loweredUrl = url.ToLowerInvariant();
        var text = CleanHtmlText(html).ToLowerInvariant();
        var genus = scientificName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

        if (IsKnownFreshwaterGenus(genus))
        {
            return AnimalReferenceEnvironment.FreshwaterTropical;
        }

        if (IsKnownMarineGenus(genus))
        {
            return AnimalReferenceEnvironment.Marine;
        }

        if (loweredUrl.Contains("eau-douce")
            || loweredUrl.Contains("freshwater")
            || text.Contains("d'eau douce")
            || text.Contains("d’eau douce")
            || text.Contains("eau douce")
            || text.Contains("aquarium communautaire, eau douce"))
        {
            return AnimalReferenceEnvironment.FreshwaterTropical;
        }

        if (defaultEnvironment == AnimalReferenceEnvironment.FreshwaterTropical
            && loweredUrl.Contains("fishipedia.fr", StringComparison.OrdinalIgnoreCase))
        {
            return defaultEnvironment;
        }

        if (loweredUrl.Contains("eau-de-mer")
            || loweredUrl.Contains("saltwater")
            || Regex.IsMatch(text, @"\b(eau de mer|eau-de-mer|poisson marin|poissons marins|aquarium marin|recifal|récifal|saltwater|reef)\b", RegexOptions.IgnoreCase))
        {
            return AnimalReferenceEnvironment.Marine;
        }

        return defaultEnvironment;
    }

    private static bool IsKnownFreshwaterGenus(string genus)
    {
        var freshwaterGenera = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Aborichthys",
            "Abramis",
            "Abramites",
            "Acanthicus",
            "Acapoeta",
            "Acarichthys",
            "Acaronia",
            "Acestridium",
            "Acnodon",
            "Huso"
        };

        return freshwaterGenera.Contains(genus);
    }

    private static bool IsKnownMarineGenus(string genus)
    {
        var marineGenera = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Abudefduf",
            "Acanthurus",
            "Amphiprion",
            "Zebrasoma"
        };

        return marineGenera.Contains(genus);
    }

    private static bool LooksLikeScientificName(string genus, string species)
    {
        if (string.IsNullOrWhiteSpace(genus) || string.IsNullOrWhiteSpace(species))
        {
            return false;
        }

        if (genus.Length < 3 || species.Length < 3)
        {
            return false;
        }
        if (!char.IsUpper(genus[0]) || species.Any(char.IsUpper))
        {
            return false;
        }
        if (!genus.All(char.IsLetter) || !species.All(ch => char.IsLetter(ch) || ch == '-'))
        {
            return false;
        }

        var blockedGenera = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Https","Http","Www","Title","Image","Video","Login","Cookie","Footer","Header","Button","Search","Filter","Article","Accueil","Contact","Panier","Compte","Menu","Home","Shop","News","Forum",
            "Les","Le","La","Des","Du","De","Un","Une","Qui","Lire","Voir","Icon","Text","Special","Informations","Recherche","Riviere","Waiting","Nous","Votre","Aidez","Refuser","Accepter","Merci"
        };
        var blockedSpecies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "com","fr","php","html","jpeg","jpg","png","svg","json","cookie","login","search","button","article","footer","header","index","page","read","view","shop","cart","admin","www",
            "plantes","poissons","scientifiques","textuelle","operation","validated","faciles","femelle","rouge","parc","est-il","accordons","plateforme","utilisation","ameliorer","pour"
        };

        if (blockedGenera.Contains(genus) || blockedSpecies.Contains(species))
        {
            return false;
        }

        return !IsRejectedPhrase(genus, species);
    }

    private static bool IsRejectedPhrase(string genus, string species)
    {
        var joined = $"{genus} {species}".ToLowerInvariant();
        var blockedPhrases = new[]
        {
            "les plantes",
            "les poissons",
            "informations scientifiques",
            "recherche textuelle",
            "qui est-il",
            "special operation",
            "text validated",
            "icon js-",
            "voir plus",
            "lire cet",
            "nous accordons",
            "votre plateforme",
            "merci pour"
        };

        return blockedPhrases.Any(joined.Contains);
    }

    private static string NormalizeScientificName(string genus, string species)
    {
        var normalizedGenus = char.ToUpperInvariant(genus[0]) + genus[1..].ToLowerInvariant();
        var normalizedSpecies = species.ToLowerInvariant();
        return $"{normalizedGenus} {normalizedSpecies}";
    }

    private static bool HasScientificContext(string html, int index, int length)
    {
        var start = Math.Max(0, index - 80);
        var end = Math.Min(html.Length, index + length + 80);
        var window = html[start..end].ToLowerInvariant();
        return window.Contains("<i")
            || window.Contains("<em")
            || window.Contains("scientific")
            || window.Contains("latin")
            || window.Contains("taxonomy")
            || window.Contains("/plant")
            || window.Contains("/poisson")
            || window.Contains("/fish")
            || window.Contains("/species");
    }

    private static async Task CleanupInvalidAutoPlantReferencesAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM PlantReferences
            WHERE (CommonName = ScientificName)
              AND PhMin IS NULL AND PhMax IS NULL
              AND GhMin IS NULL AND GhMax IS NULL
              AND KhMin IS NULL AND KhMax IS NULL
              AND TemperatureMin IS NULL AND TemperatureMax IS NULL
              AND AmmoniaMin IS NULL AND AmmoniaMax IS NULL
              AND NitritesMin IS NULL AND NitritesMax IS NULL
              AND NitratesMin IS NULL AND NitratesMax IS NULL
              AND VolumeMinLiters IS NULL;
            """;
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CleanupInvalidAutoAnimalReferencesAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM AnimalReferences
            WHERE
                (
                    (CommonName = ScientificName)
                    AND (
                        (CASE WHEN PhMin IS NOT NULL OR PhMax IS NOT NULL THEN 1 ELSE 0 END) +
                        (CASE WHEN GhMin IS NOT NULL OR GhMax IS NOT NULL THEN 1 ELSE 0 END) +
                        (CASE WHEN KhMin IS NOT NULL OR KhMax IS NOT NULL THEN 1 ELSE 0 END) +
                        (CASE WHEN TemperatureMin IS NOT NULL OR TemperatureMax IS NOT NULL THEN 1 ELSE 0 END) +
                        (CASE WHEN AmmoniaMin IS NOT NULL OR AmmoniaMax IS NOT NULL THEN 1 ELSE 0 END) +
                        (CASE WHEN NitritesMin IS NOT NULL OR NitritesMax IS NOT NULL THEN 1 ELSE 0 END) +
                        (CASE WHEN NitratesMin IS NOT NULL OR NitratesMax IS NOT NULL THEN 1 ELSE 0 END) +
                        (CASE WHEN VolumeMinLiters IS NOT NULL THEN 1 ELSE 0 END)
                    ) < 4
                )
                OR LOWER(CommonName) LIKE 'les %'
                OR LOWER(CommonName) LIKE 'icon %'
                OR LOWER(CommonName) LIKE '%recherche textuelle%'
                OR LOWER(CommonName) LIKE '%informations scientifiques%'
                OR LOWER(CommonName) LIKE '%special operation%'
                OR LOWER(CommonName) LIKE '%text validated%'
                OR LOWER(CommonName) LIKE '%qui est-il%'
                OR LOWER(CommonName) LIKE '%voir plus%'
                OR LOWER(CommonName) LIKE '%lire cet%'
                OR LOWER(CommonName) LIKE '%waiting for%'
                OR LOWER(CommonName) LIKE '%cliquer ici%'
                OR LOWER(CommonName) LIKE '%editer%'
                OR LOWER(CommonName) LIKE '%erreur%'
                OR LOWER(CommonName) LIKE '%fishipedia logo%'
                OR LOWER(CommonName) LIKE '%faire une%'
                OR LOWER(ScientificName) = 'nous accordons'
                OR LOWER(ScientificName) LIKE 'votre %'
                OR LOWER(ScientificName) LIKE 'aidez %';
            """;
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> TryGetStringWithRetryAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= WebRequestMaxAttempts; attempt++)
        {
            try
            {
                return await httpClient.GetStringAsync(url, cancellationToken);
            }
            catch when (attempt < WebRequestMaxAttempts)
            {
                await Task.Delay(WebRetryDelay, cancellationToken);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static readonly IReadOnlyList<PlantReference> SeedPlantReferences =
    [
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Anubias", ScientificName = "Anubias barteri", PhMin = 6.0m, PhMax = 7.8m, GhMin = 2m, GhMax = 12m, KhMin = 1m, KhMax = 8m, TemperatureMin = 22m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 30m, VolumeMinLiters = 40, LightNeed = "Faible a moyenne", Co2Need = "Faible", FertilizationNeed = "Faible", GrowthSpeed = "Lente", RecommendedPlacement = "Avant / milieu", Behavior = "Robuste", Compatibility = "Compatible communautaire", SourceUrl = "https://www.fishipedia.fr/fr/plants/anubias-barteri" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Bacopa monnieri", ScientificName = "Bacopa monnieri", PhMin = 6.0m, PhMax = 7.8m, GhMin = 5m, GhMax = 25m, KhMin = 2m, KhMax = 12m, TemperatureMin = 15m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 30m, VolumeMinLiters = 60, LightNeed = "Moyenne a forte", Co2Need = "Optionnel", FertilizationNeed = "Moyenne", GrowthSpeed = "Moyenne", RecommendedPlacement = "Milieu / arriere", Behavior = "Tige", Compatibility = "Bonne pour debutants", SourceUrl = "https://www.fishipedia.fr/fr/plants/bacopa-monnieri" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Ceratophyllum", ScientificName = "Ceratophyllum demersum", PhMin = 6.0m, PhMax = 8.0m, GhMin = 5m, GhMax = 20m, KhMin = 2m, KhMax = 12m, TemperatureMin = 1m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 40m, VolumeMinLiters = 40, LightNeed = "Moyenne", Co2Need = "Faible", FertilizationNeed = "Faible", GrowthSpeed = "Rapide", RecommendedPlacement = "Flottante / arriere", Behavior = "Tres rapide", Compatibility = "Excellente anti-algues", SourceUrl = "https://www.fishipedia.fr/fr/plants/ceratophyllum-demersum" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Ceratopteris", ScientificName = "Ceratopteris thalictroides", PhMin = 5.0m, PhMax = 9.0m, GhMin = 5m, GhMax = 25m, KhMin = 2m, KhMax = 12m, TemperatureMin = 22m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 30m, VolumeMinLiters = 80, LightNeed = "Moyenne a forte", Co2Need = "Faible", FertilizationNeed = "Moyenne", GrowthSpeed = "Rapide", RecommendedPlacement = "Surface / arriere", Behavior = "Peut flotter", Compatibility = "Bonne contre nitrates", SourceUrl = "https://www.fishipedia.fr/fr/plants/ceratopteris-thalictroides" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Aponogeton crispus", ScientificName = "Aponogeton crispus", PhMin = 6.0m, PhMax = 7.5m, GhMin = 1m, GhMax = 12m, KhMin = 1m, KhMax = 8m, TemperatureMin = 20m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 25m, VolumeMinLiters = 80, LightNeed = "Moyenne", Co2Need = "Optionnel", FertilizationNeed = "Moyenne", GrowthSpeed = "Moyenne", RecommendedPlacement = "Milieu / arriere", Behavior = "Bulbe", Compatibility = "Bonne en bac calme", SourceUrl = "https://www.fishipedia.fr/fr/plants/aponogeton-crispus" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Aponogeton madagascariensis", ScientificName = "Aponogeton madagascariensis", PhMin = 5.0m, PhMax = 7.5m, GhMin = 1m, GhMax = 10m, KhMin = 1m, KhMax = 8m, TemperatureMin = 20m, TemperatureMax = 26m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 25m, VolumeMinLiters = 120, LightNeed = "Moyenne", Co2Need = "Optionnel", FertilizationNeed = "Moyenne", GrowthSpeed = "Moyenne", RecommendedPlacement = "Milieu / arriere", Behavior = "Feuille en dentelle", Compatibility = "Demande eau propre", SourceUrl = "https://www.fishipedia.fr/fr/plants/aponogeton-madagascariensis" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Anubias gilletii", ScientificName = "Anubias gilletii", PhMin = 5.5m, PhMax = 8.0m, GhMin = 2m, GhMax = 15m, KhMin = 1m, KhMax = 10m, TemperatureMin = 22m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 30m, VolumeMinLiters = 70, LightNeed = "Faible a moyenne", Co2Need = "Faible", FertilizationNeed = "Faible", GrowthSpeed = "Lente", RecommendedPlacement = "Roche / racine", Behavior = "Epiphyte", Compatibility = "Compatibilite elevee", SourceUrl = "https://www.fishipedia.fr/fr/plants/anubias-gilletii" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Alternanthera reineckii", ScientificName = "Alternanthera reineckii", PhMin = 6.0m, PhMax = 7.5m, GhMin = 2m, GhMax = 12m, KhMin = 1m, KhMax = 8m, TemperatureMin = 22m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 25m, VolumeMinLiters = 80, LightNeed = "Forte", Co2Need = "Moyen", FertilizationNeed = "Moyenne a elevee", GrowthSpeed = "Moyenne", RecommendedPlacement = "Milieu", Behavior = "Rouge decorative", Compatibility = "Compatible communautaire", SourceUrl = "https://tropica.com/en/plants/plantdetails/alternantherareineckii(023)/" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Eleocharis acicularis", ScientificName = "Eleocharis acicularis", PhMin = 6.0m, PhMax = 7.5m, GhMin = 2m, GhMax = 15m, KhMin = 1m, KhMax = 10m, TemperatureMin = 20m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 25m, VolumeMinLiters = 60, LightNeed = "Moyenne a forte", Co2Need = "Moyen", FertilizationNeed = "Moyenne", GrowthSpeed = "Moyenne", RecommendedPlacement = "Avant", Behavior = "Gazonnante", Compatibility = "Bonne en aquascaping", SourceUrl = "https://tropica.com/en/plants/plantdetails/eleocharisacicularis(132)/" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Rotala rotundifolia", ScientificName = "Rotala rotundifolia", PhMin = 5.5m, PhMax = 7.5m, GhMin = 1m, GhMax = 12m, KhMin = 1m, KhMax = 8m, TemperatureMin = 22m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 25m, VolumeMinLiters = 70, LightNeed = "Moyenne a forte", Co2Need = "Moyen", FertilizationNeed = "Moyenne", GrowthSpeed = "Rapide", RecommendedPlacement = "Arriere", Behavior = "Tige fine", Compatibility = "Bonne avec taille reguliere", SourceUrl = "https://tropica.com/en/plants/plantdetails/rotalarotundifolia(033)/" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Staurogyne repens", ScientificName = "Staurogyne repens", PhMin = 6.0m, PhMax = 7.5m, GhMin = 2m, GhMax = 12m, KhMin = 1m, KhMax = 8m, TemperatureMin = 20m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 25m, VolumeMinLiters = 50, LightNeed = "Moyenne", Co2Need = "Moyen", FertilizationNeed = "Moyenne", GrowthSpeed = "Moyenne", RecommendedPlacement = "Avant / milieu", Behavior = "Compacte", Compatibility = "Bonne en premier plan", SourceUrl = "https://tropica.com/en/plants/plantdetails/staurogyne-repens(049)/" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Taxiphyllum barbieri", ScientificName = "Taxiphyllum barbieri", PhMin = 5.0m, PhMax = 8.0m, GhMin = 1m, GhMax = 20m, KhMin = 0m, KhMax = 12m, TemperatureMin = 18m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 30m, VolumeMinLiters = 20, LightNeed = "Faible a moyenne", Co2Need = "Faible", FertilizationNeed = "Faible", GrowthSpeed = "Moyenne", RecommendedPlacement = "Racine / roche", Behavior = "Mousse", Compatibility = "Excellente pour crevettes", SourceUrl = "https://tropica.com/en/plants/plantdetails/taxiphyllumbarbieri(003)/" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Bolbitis heudelotii", ScientificName = "Bolbitis heudelotii", PhMin = 5.5m, PhMax = 7.5m, GhMin = 1m, GhMax = 10m, KhMin = 1m, KhMax = 8m, TemperatureMin = 22m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 25m, VolumeMinLiters = 80, LightNeed = "Moyenne", Co2Need = "Moyen", FertilizationNeed = "Moyenne", GrowthSpeed = "Lente", RecommendedPlacement = "Racines / roches", Behavior = "Fougere aquatique", Compatibility = "Preferer courant doux", SourceUrl = "https://tropica.com/en/plants/plantdetails/bolbitisheudelotii(053)/" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Pogostemon helferi", ScientificName = "Pogostemon helferi", PhMin = 6.0m, PhMax = 7.5m, GhMin = 1m, GhMax = 12m, KhMin = 1m, KhMax = 8m, TemperatureMin = 22m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 25m, VolumeMinLiters = 50, LightNeed = "Moyenne a forte", Co2Need = "Moyen", FertilizationNeed = "Moyenne", GrowthSpeed = "Moyenne", RecommendedPlacement = "Avant", Behavior = "Rosette ondulee", Compatibility = "Bonne en aquascaping", SourceUrl = "https://tropica.com/en/plants/plantdetails/pogostemonhelferi(056)/" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Hydrocotyle tripartita", ScientificName = "Hydrocotyle tripartita", PhMin = 5.8m, PhMax = 7.5m, GhMin = 1m, GhMax = 12m, KhMin = 1m, KhMax = 8m, TemperatureMin = 20m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 25m, VolumeMinLiters = 40, LightNeed = "Moyenne a forte", Co2Need = "Moyen", FertilizationNeed = "Moyenne", GrowthSpeed = "Rapide", RecommendedPlacement = "Avant / milieu", Behavior = "Tapis rampant", Compatibility = "Bonne en aquascaping", SourceUrl = "https://tropica.com/en/plants/plantdetails/?id=18751" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Anubias nana petite", ScientificName = "Anubias barteri var. nana", PhMin = 5.5m, PhMax = 9.0m, GhMin = 2m, GhMax = 15m, KhMin = 1m, KhMax = 10m, TemperatureMin = 20m, TemperatureMax = 30m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 30m, VolumeMinLiters = 20, LightNeed = "Faible", Co2Need = "Faible", FertilizationNeed = "Faible", GrowthSpeed = "Lente", RecommendedPlacement = "Avant / decor", Behavior = "Epiphyte", Compatibility = "Tres adaptable", SourceUrl = "https://www.aquaplante.fr/anubias/aquaplante-plantes/3783-anubias-barteri-nana-petite-premium-plante-aquarium-facile.html" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Anubias barteri (Aquaplante)", ScientificName = "Anubias barteri var. glabra", PhMin = 6.0m, PhMax = 8.0m, GhMin = 2m, GhMax = 15m, KhMin = 1m, KhMax = 10m, TemperatureMin = 10m, TemperatureMax = 30m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 30m, VolumeMinLiters = 40, LightNeed = "Faible", Co2Need = "Faible", FertilizationNeed = "Faible", GrowthSpeed = "Lente", RecommendedPlacement = "Roche / racine", Behavior = "Feuilles coriaces", Compatibility = "Bonne avec poissons phytophages", SourceUrl = "https://www.aquaplante.fr/fr/achat-plantes-aquatiques/aquaplante-plantes/764-plante-d-aquarium-resistante-anubias-barteri.html" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Cryptocoryne balansae", ScientificName = "Cryptocoryne crispatula var. balansae", PhMin = 6.0m, PhMax = 7.8m, GhMin = 3m, GhMax = 15m, KhMin = 2m, KhMax = 10m, TemperatureMin = 22m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 30m, VolumeMinLiters = 100, LightNeed = "Moyenne", Co2Need = "Optionnel", FertilizationNeed = "Moyenne", GrowthSpeed = "Moyenne", RecommendedPlacement = "Arriere", Behavior = "Grandes feuilles rubanees", Compatibility = "Bonne en bac communautaire", SourceUrl = "https://www.aquabiance.fr/plante/144-cryptocoryne-balansae.html" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Cryptocoryne usteriana", ScientificName = "Cryptocoryne usteriana", PhMin = 6.0m, PhMax = 7.8m, GhMin = 3m, GhMax = 15m, KhMin = 2m, KhMax = 10m, TemperatureMin = 22m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 30m, VolumeMinLiters = 120, LightNeed = "Moyenne", Co2Need = "Optionnel", FertilizationNeed = "Moyenne", GrowthSpeed = "Moyenne", RecommendedPlacement = "Milieu / arriere", Behavior = "Feuilles ondulees", Compatibility = "Bonne avec poissons calmes", SourceUrl = "https://aquarilis.fr/plantes/rosette/cryptocoryne-usteriana/" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Microsorum Windelov", ScientificName = "Microsorum pteropus 'Windelov'", PhMin = 6.0m, PhMax = 7.8m, GhMin = 2m, GhMax = 15m, KhMin = 1m, KhMax = 10m, TemperatureMin = 22m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 25m, VolumeMinLiters = 40, LightNeed = "Faible", Co2Need = "Faible", FertilizationNeed = "Faible", GrowthSpeed = "Lente", RecommendedPlacement = "Roche / racine", Behavior = "Fougere epiphyte", Compatibility = "Tres facile", SourceUrl = "https://tropica.com/en/plants/plantdetails/?id=4423" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Vallisneria gigantea", ScientificName = "Vallisneria americana 'Gigantea'", PhMin = 6.5m, PhMax = 8.0m, GhMin = 4m, GhMax = 18m, KhMin = 3m, KhMax = 12m, TemperatureMin = 20m, TemperatureMax = 30m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 35m, VolumeMinLiters = 120, LightNeed = "Faible a moyenne", Co2Need = "Faible", FertilizationNeed = "Moyenne", GrowthSpeed = "Rapide", RecommendedPlacement = "Arriere", Behavior = "Longues feuilles", Compatibility = "Bonne avec vivipares", SourceUrl = "https://tropica.com/en/plants/plantdetails/?id=4501" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Riccia fluitans", ScientificName = "Riccia fluitans", PhMin = 6.0m, PhMax = 7.8m, GhMin = 2m, GhMax = 15m, KhMin = 1m, KhMax = 10m, TemperatureMin = 20m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 25m, VolumeMinLiters = 20, LightNeed = "Moyenne", Co2Need = "Moyen", FertilizationNeed = "Moyenne", GrowthSpeed = "Moyenne", RecommendedPlacement = "Surface / gazon", Behavior = "Hepatique flottante", Compatibility = "Bonne pour alevins", SourceUrl = "https://tropica.com/en/plants/plantdetails/?id=4386" },
        new() { Environment = PlantReferenceEnvironment.FreshwaterTropical, CommonName = "Cryptocoryne wendtii Tropica", ScientificName = "Cryptocoryne wendtii 'Tropica'", PhMin = 6.0m, PhMax = 8.0m, GhMin = 3m, GhMax = 18m, KhMin = 2m, KhMax = 10m, TemperatureMin = 22m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.05m, NitritesMin = 0m, NitritesMax = 0.1m, NitratesMin = 5m, NitratesMax = 30m, VolumeMinLiters = 30, LightNeed = "Faible a moyenne", Co2Need = "Faible", FertilizationNeed = "Moyenne", GrowthSpeed = "Moyenne", RecommendedPlacement = "Avant / milieu", Behavior = "Rosette compacte", Compatibility = "Tres facile", SourceUrl = "https://tropica.com/en/plants/plantdetails/CryptocorynewendtiiTropica%28109E%29/4564" },
        new() { Environment = PlantReferenceEnvironment.Marine, CommonName = "Halimeda", ScientificName = "Halimeda opuntia", PhMin = 8.0m, PhMax = 8.4m, GhMin = 7m, GhMax = 12m, KhMin = 7m, KhMax = 12m, TemperatureMin = 23m, TemperatureMax = 27m, AmmoniaMin = 0m, AmmoniaMax = 0.02m, NitritesMin = 0m, NitritesMax = 0.05m, NitratesMin = 0m, NitratesMax = 10m, VolumeMinLiters = 150, LightNeed = "Forte", Co2Need = "Faible", FertilizationNeed = "Moyenne", GrowthSpeed = "Moyenne", RecommendedPlacement = "Roches vivantes", Behavior = "Calcifiante", Compatibility = "Compatible recifal", SourceUrl = "https://en.wikipedia.org/wiki/Halimeda" },
        new() { Environment = PlantReferenceEnvironment.Marine, CommonName = "Caulerpa prolifera", ScientificName = "Caulerpa prolifera", PhMin = 8.0m, PhMax = 8.4m, GhMin = 7m, GhMax = 12m, KhMin = 7m, KhMax = 12m, TemperatureMin = 23m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.02m, NitritesMin = 0m, NitritesMax = 0.05m, NitratesMin = 1m, NitratesMax = 15m, VolumeMinLiters = 200, LightNeed = "Moyenne a forte", Co2Need = "Moyen", FertilizationNeed = "Moyenne", GrowthSpeed = "Rapide", RecommendedPlacement = "Refuge / decor", Behavior = "Peut devenir envahissante", Compatibility = "A surveiller avec coraux", SourceUrl = "https://en.wikipedia.org/wiki/Caulerpa_prolifera" }
    ];

    private static readonly IReadOnlyList<AnimalReference> SeedAnimalReferences =
    [
        new() { Environment = AnimalReferenceEnvironment.FreshwaterTropical, CommonName = "Neon bleu", ScientificName = "Paracheirodon innesi", PhMin = 6.0m, PhMax = 7.2m, GhMin = 1m, GhMax = 10m, KhMin = 1m, KhMax = 6m, TemperatureMin = 20m, TemperatureMax = 26m, AmmoniaMin = 0m, AmmoniaMax = 0.02m, NitritesMin = 0m, NitritesMax = 0.05m, NitratesMin = 0m, NitratesMax = 20m, VolumeMinLiters = 80, Behavior = "Banc paisible", Compatibility = "Communautaire calme", SourceUrl = "https://www.fishipedia.fr/fr/poissons/paracheirodon-innesi" },
        new() { Environment = AnimalReferenceEnvironment.FreshwaterTropical, CommonName = "Guppy", ScientificName = "Poecilia reticulata", PhMin = 7.0m, PhMax = 8.0m, GhMin = 8m, GhMax = 20m, KhMin = 4m, KhMax = 12m, TemperatureMin = 22m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.02m, NitritesMin = 0m, NitritesMax = 0.05m, NitratesMin = 0m, NitratesMax = 25m, VolumeMinLiters = 80, Behavior = "Vif", Compatibility = "Eviter poissons agressifs", SourceUrl = "https://www.fishipedia.fr/fr/poissons/poecilia-reticulata" },
        new() { Environment = AnimalReferenceEnvironment.FreshwaterTropical, CommonName = "Scalaire", ScientificName = "Pterophyllum scalare", PhMin = 6.0m, PhMax = 7.4m, GhMin = 2m, GhMax = 12m, KhMin = 1m, KhMax = 8m, TemperatureMin = 25m, TemperatureMax = 30m, AmmoniaMin = 0m, AmmoniaMax = 0.02m, NitritesMin = 0m, NitritesMax = 0.05m, NitratesMin = 0m, NitratesMax = 20m, VolumeMinLiters = 240, Behavior = "Hierarchique", Compatibility = "Avec especes de taille suffisante", SourceUrl = "https://www.fishipedia.fr/fr/poissons/pterophyllum-scalare" },
        new() { Environment = AnimalReferenceEnvironment.FreshwaterTropical, CommonName = "Betta splendens", ScientificName = "Betta splendens", PhMin = 6.0m, PhMax = 7.5m, GhMin = 3m, GhMax = 12m, KhMin = 1m, KhMax = 8m, TemperatureMin = 24m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.02m, NitritesMin = 0m, NitritesMax = 0.05m, NitratesMin = 0m, NitratesMax = 20m, VolumeMinLiters = 20, Behavior = "Territorial", Compatibility = "Male seul", SourceUrl = "https://www.fishipedia.fr/fr/poissons/betta-splendens" },
        new() { Environment = AnimalReferenceEnvironment.FreshwaterTropical, CommonName = "Corydoras paleatus", ScientificName = "Corydoras paleatus", PhMin = 6.0m, PhMax = 7.5m, GhMin = 2m, GhMax = 15m, KhMin = 1m, KhMax = 8m, TemperatureMin = 22m, TemperatureMax = 26m, AmmoniaMin = 0m, AmmoniaMax = 0.02m, NitritesMin = 0m, NitritesMax = 0.05m, NitratesMin = 0m, NitratesMax = 20m, VolumeMinLiters = 80, Behavior = "Gregaire de fond", Compatibility = "En groupe 6+", SourceUrl = "https://www.fishipedia.fr/fr/poissons/corydoras-paleatus" },
        new() { Environment = AnimalReferenceEnvironment.FreshwaterTropical, CommonName = "Ancistrus", ScientificName = "Ancistrus cf. cirrhosus", PhMin = 6.0m, PhMax = 7.5m, GhMin = 2m, GhMax = 15m, KhMin = 1m, KhMax = 8m, TemperatureMin = 23m, TemperatureMax = 28m, AmmoniaMin = 0m, AmmoniaMax = 0.02m, NitritesMin = 0m, NitritesMax = 0.05m, NitratesMin = 0m, NitratesMax = 25m, VolumeMinLiters = 120, Behavior = "Territorial modere", Compatibility = "Bonne en communautaire", SourceUrl = "https://www.aquachange.fr/poisson_fiche_aquarium.php?id=22" },
        new() { Environment = AnimalReferenceEnvironment.FreshwaterTropical, CommonName = "Discus", ScientificName = "Symphysodon aequifasciatus", PhMin = 5.5m, PhMax = 7.0m, GhMin = 1m, GhMax = 8m, KhMin = 1m, KhMax = 4m, TemperatureMin = 28m, TemperatureMax = 31m, AmmoniaMin = 0m, AmmoniaMax = 0.02m, NitritesMin = 0m, NitritesMax = 0.05m, NitratesMin = 0m, NitratesMax = 15m, VolumeMinLiters = 350, Behavior = "Gregaire", Compatibility = "Bac specifique recommande", SourceUrl = "https://www.fishipedia.fr/fr/poissons/symphysodon-aequifasciatus" },
        new() { Environment = AnimalReferenceEnvironment.Marine, CommonName = "Poisson clown ocelle", ScientificName = "Amphiprion ocellaris", PhMin = 8.0m, PhMax = 8.4m, GhMin = 7m, GhMax = 12m, KhMin = 7m, KhMax = 12m, TemperatureMin = 24m, TemperatureMax = 27m, AmmoniaMin = 0m, AmmoniaMax = 0.02m, NitritesMin = 0m, NitritesMax = 0.05m, NitratesMin = 0m, NitratesMax = 10m, VolumeMinLiters = 120, Behavior = "Territorial modere", Compatibility = "Compatible recifal", SourceUrl = "https://www.fishipedia.fr/fr/poissons/amphiprion-ocellaris" },
        new() { Environment = AnimalReferenceEnvironment.Marine, CommonName = "Chirurgien jaune", ScientificName = "Zebrasoma flavescens", PhMin = 8.0m, PhMax = 8.4m, GhMin = 7m, GhMax = 12m, KhMin = 7m, KhMax = 12m, TemperatureMin = 24m, TemperatureMax = 27m, AmmoniaMin = 0m, AmmoniaMax = 0.02m, NitritesMin = 0m, NitritesMax = 0.05m, NitratesMin = 0m, NitratesMax = 10m, VolumeMinLiters = 450, Behavior = "Actif", Compatibility = "Peut etre territorial", SourceUrl = "https://www.fishipedia.fr/fr/poissons/zebrasoma-flavescens" },
        new() { Environment = AnimalReferenceEnvironment.Marine, CommonName = "Chirurgien bleu", ScientificName = "Acanthurus leucosternon", PhMin = 8.0m, PhMax = 8.4m, GhMin = 7m, GhMax = 12m, KhMin = 7m, KhMax = 12m, TemperatureMin = 24m, TemperatureMax = 27m, AmmoniaMin = 0m, AmmoniaMax = 0.02m, NitritesMin = 0m, NitritesMax = 0.05m, NitratesMin = 0m, NitratesMax = 10m, VolumeMinLiters = 600, Behavior = "Nageur rapide", Compatibility = "Espace important", SourceUrl = "https://www.aquaportail.com/fiche-poisson-808-acanthurus-leucosternon.html" }
    ];
}

using ADAqua.Domain;
using MySqlConnector;

namespace ADAqua.Infrastructure;

public sealed class MySqlAquariumRepository(string connectionString) : IAquariumRepository
{
    private static readonly HashSet<string> ChildTables =
    [
        "WaterMeasurements",
        "AquariumPlants",
        "PopulationMembers"
    ];

    public async Task<IReadOnlyList<Aquarium>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var aquariums = new Dictionary<Guid, Aquarium>();
        await LoadAquariumsAsync(connection, aquariums, cancellationToken);
        await LoadMeasurementsAsync(connection, aquariums, cancellationToken);
        await LoadPlantsAsync(connection, aquariums, cancellationToken);
        await LoadPopulationAsync(connection, aquariums, cancellationToken);

        return aquariums.Values.OrderBy(aquarium => aquarium.Name).ToList();
    }

    public async Task SaveAsync(Aquarium aquarium, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
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
    }

    private static async Task LoadAquariumsAsync(MySqlConnection connection, Dictionary<Guid, Aquarium> aquariums, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("SELECT Id, Name, VolumeLiters, WaterType, StartedOn, Notes FROM Aquariums;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var aquarium = new Aquarium
            {
                Id = Guid.Parse(reader.GetString(0)),
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
        await using var command = new MySqlCommand("SELECT Id, AquariumId, MeasuredAt, Ammonia, Nitrites, Nitrates, Ph, Gh, Kh, TemperatureCelsius, Notes FROM WaterMeasurements;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken) && aquariums.TryGetValue(Guid.Parse(reader.GetString(1)), out var aquarium))
        {
            aquarium.Measurements.Add(new WaterParameters
            {
                Id = Guid.Parse(reader.GetString(0)),
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
        await using var command = new MySqlCommand("SELECT Id, AquariumId, CommonName, ScientificName, GrowthSpeed, LightNeed, Notes FROM AquariumPlants;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken) && aquariums.TryGetValue(Guid.Parse(reader.GetString(1)), out var aquarium))
        {
            aquarium.Plants.Add(new AquariumPlant
            {
                Id = Guid.Parse(reader.GetString(0)),
                CommonName = reader.GetString(2),
                ScientificName = reader.GetString(3),
                GrowthSpeed = Enum.Parse<PlantGrowthSpeed>(reader.GetString(4)),
                LightNeed = reader.GetString(5),
                Notes = reader.GetString(6)
            });
        }
    }

    private static async Task LoadPopulationAsync(MySqlConnection connection, Dictionary<Guid, Aquarium> aquariums, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("SELECT Id, AquariumId, SpeciesName, CommonName, Type, Quantity, Notes FROM PopulationMembers;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken) && aquariums.TryGetValue(Guid.Parse(reader.GetString(1)), out var aquarium))
        {
            aquarium.Population.Add(new PopulationMember
            {
                Id = Guid.Parse(reader.GetString(0)),
                SpeciesName = reader.GetString(2),
                CommonName = reader.GetString(3),
                Type = Enum.Parse<PopulationType>(reader.GetString(4)),
                Quantity = reader.GetInt32(5),
                Notes = reader.GetString(6)
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
                INSERT INTO AquariumPlants (Id, AquariumId, CommonName, ScientificName, GrowthSpeed, LightNeed, Notes)
                VALUES (@Id, @AquariumId, @CommonName, @ScientificName, @GrowthSpeed, @LightNeed, @Notes)
                ON DUPLICATE KEY UPDATE
                    CommonName = VALUES(CommonName),
                    ScientificName = VALUES(ScientificName),
                    GrowthSpeed = VALUES(GrowthSpeed),
                    LightNeed = VALUES(LightNeed),
                    Notes = VALUES(Notes);
                """,
                cancellationToken,
                Parameter("@Id", plant.Id.ToString()),
                Parameter("@AquariumId", aquarium.Id.ToString()),
                Parameter("@CommonName", plant.CommonName),
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
                INSERT INTO PopulationMembers (Id, AquariumId, SpeciesName, CommonName, Type, Quantity, Notes)
                VALUES (@Id, @AquariumId, @SpeciesName, @CommonName, @Type, @Quantity, @Notes)
                ON DUPLICATE KEY UPDATE
                    SpeciesName = VALUES(SpeciesName),
                    CommonName = VALUES(CommonName),
                    Type = VALUES(Type),
                    Quantity = VALUES(Quantity),
                    Notes = VALUES(Notes);
                """,
                cancellationToken,
                Parameter("@Id", member.Id.ToString()),
                Parameter("@AquariumId", aquarium.Id.ToString()),
                Parameter("@SpeciesName", member.SpeciesName),
                Parameter("@CommonName", member.CommonName),
                Parameter("@Type", member.Type.ToString()),
                Parameter("@Quantity", member.Quantity),
                Parameter("@Notes", member.Notes));
        }

        await DeleteMissingChildrenAsync(connection, transaction, "PopulationMembers", aquarium.Id, aquarium.Population.Select(member => member.Id), cancellationToken);
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

    private static decimal? ReadNullableDecimal(MySqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    }
}

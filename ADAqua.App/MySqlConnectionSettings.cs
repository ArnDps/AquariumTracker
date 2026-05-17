using MySqlConnector;

namespace ADAqua.App;

public sealed class MySqlConnectionSettings
{
    public string Server { get; set; } = "localhost";
    public uint Port { get; set; } = 3306;
    public string Database { get; set; } = "ADAqua";
    public string UserId { get; set; } = "root";
    public string Password { get; set; } = string.Empty;

    public string BuildConnectionString()
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = Server,
            Port = Port,
            Database = Database,
            UserID = UserId,
            Password = Password,
            AllowUserVariables = true
        };

        return builder.ConnectionString;
    }

    public static MySqlConnectionSettings FromConnectionString(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        return new MySqlConnectionSettings
        {
            Server = builder.Server,
            Port = builder.Port,
            Database = builder.Database,
            UserId = builder.UserID,
            Password = builder.Password
        };
    }
}

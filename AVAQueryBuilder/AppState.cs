namespace AVAQueryBuilder;

public static class AppState
{
    public static string ConnectionString { get; set; } = string.Empty;

    /// <summary>The engine the connection string is for; it decides the SQL dialect too.</summary>
    public static DbEngine Engine { get; set; } = DbEngine.SqlServer;

    /// <summary>The saved connection's name, when the database was chosen by name (AI › Database Connections), or null.</summary>
    public static string? ConnectionName { get; set; }
}

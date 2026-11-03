namespace AVAQueryBuilder;

public static class AppState
{
    public static string ConnectionString { get; set; } = string.Empty;

    /// <summary>The engine the connection string is for; it decides the SQL dialect too.</summary>
    public static DbEngine Engine { get; set; } = DbEngine.SqlServer;
}

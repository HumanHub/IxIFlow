namespace IxIFlow.Tests.Infrastructure;

/// <summary>Removes a test database and its sidecars after their owners have disposed.</summary>
public static class SqliteTestFiles
{
    public static Task DeleteAsync(string folder)
    {
        Directory.Delete(folder, recursive: true);
        return Task.CompletedTask;
    }
}

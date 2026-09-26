namespace Order.API.ContractTests.TestSupport;

/// <summary>
/// The committed pacts/ folder at the repository root: the consumer writes its pact files there,
/// and the producers' verification tests read them from there (no Pact Broker in this project).
/// </summary>
public static class PactFolder
{
    private const string SolutionFileName = "DotnetMicroservicesTutorial.sln";

    public static string Path { get; } = Locate();

    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, SolutionFileName)))
            {
                return System.IO.Path.Combine(directory.FullName, "pacts");
            }
        }

        throw new InvalidOperationException($"No {SolutionFileName} above {AppContext.BaseDirectory}.");
    }
}

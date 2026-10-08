using Xunit;

namespace Cimian.Tests;

/// <summary>
/// Tests that swap Console.Out. xUnit runs test classes in parallel, and Console.Out
/// is process-wide, so two such classes running at once read each other's output.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ConsoleOutputCollection
{
    public const string Name = "Console output";
}

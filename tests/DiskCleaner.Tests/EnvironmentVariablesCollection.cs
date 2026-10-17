using Xunit;

namespace DiskCleaner.Tests;

/// <summary>Тесты, меняющие окружение процесса, не выполняются параллельно с другими коллекциями.</summary>
[CollectionDefinition("Environment variables", DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection
{
}

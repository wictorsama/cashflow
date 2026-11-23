namespace CashFlow.Consolidation.Integration.Tests;

/// <summary>
/// One set of PostgreSQL, RabbitMQ and Redis containers for every test class, instead of one set per class: the
/// classes then also run one after another, so they do not compete for the machine's CPU and memory (the end-to-end
/// tests wait on message delivery and time out when starved).
/// </summary>
[CollectionDefinition(Name)]
public sealed class SharedInfrastructure : ICollectionFixture<InfrastructureFixture>
{
    public const string Name = "Infrastructure";
}

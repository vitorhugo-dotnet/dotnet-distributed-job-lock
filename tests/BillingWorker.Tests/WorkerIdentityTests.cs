using BillingWorker;
using Microsoft.Extensions.Configuration;

namespace BillingWorker.Tests;

public class WorkerIdentityTests
{
    [Fact]
    public void UsaWorkerIdDaConfiguracaoQuandoDefinido()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:Id"] = "worker-2" })
            .Build();

        Assert.Equal("worker-2", WorkerIdentity.FromEnvironment(config).Id);
    }

    [Fact]
    public void CaiParaOHostnameQuandoNaoConfigurado()
    {
        var config = new ConfigurationBuilder().Build();

        Assert.Equal(Environment.MachineName, WorkerIdentity.FromEnvironment(config).Id);
    }

    [Fact]
    public void IgnoraWorkerIdEmBranco()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:Id"] = "   " })
            .Build();

        Assert.Equal(Environment.MachineName, WorkerIdentity.FromEnvironment(config).Id);
    }
}

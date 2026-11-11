using DevPortal.Discovery;

namespace DevPortal.Tests;

public sealed class InstanceCatalogTests
{
    [Fact]
    public void Container_instance_carries_its_compose_project_and_service()
    {
        var now = DateTimeOffset.UtcNow;
        var container = new ContainerInfo("id", "shop-db-1", "postgres:16", "running", "Up 2 hours", now.AddHours(-2), "shop", "db", null, []);

        var instance = InstanceCatalog.ContainerTemplate(container, now);

        Assert.Equal("shop", instance.ComposeProject);
        Assert.Equal("db", instance.ComposeService);
        Assert.Equal(7200, instance.UptimeSeconds);
    }
}

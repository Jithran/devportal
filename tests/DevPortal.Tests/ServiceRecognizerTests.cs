using System.Text.Json;
using DevPortal.Discovery;

namespace DevPortal.Tests;

public sealed class ServiceRecognizerTests
{
    [Theory]
    [InlineData("mariadb:11", null, 3306, null, ServiceKind.MariaDb, ServiceConfidence.Known)]
    [InlineData("mysql:8", null, 13306, 3306, ServiceKind.MySql, ServiceConfidence.Known)]
    [InlineData("docker.io/library/postgres:16", null, 5432, 5432, ServiceKind.Postgres, ServiceConfidence.Known)]
    [InlineData("docker.dragonflydb.io/dragonflydb/dragonfly:latest", null, 6379, 6379, ServiceKind.Dragonfly, ServiceConfidence.Known)]
    [InlineData("redis:7", null, 6380, 6379, ServiceKind.Redis, ServiceConfidence.Known)]
    [InlineData("ghcr.io/axllent/mailpit:v1.20", null, 1025, 1025, ServiceKind.Mailpit, ServiceConfidence.Known)]
    [InlineData("mongo:7", null, 27017, 27017, ServiceKind.MongoDb, ServiceConfidence.Known)]
    [InlineData(null, "mysqld.exe", 3307, null, ServiceKind.MySql, ServiceConfidence.Known)]
    [InlineData(null, "postgres.exe", 5433, null, ServiceKind.Postgres, ServiceConfidence.Known)]
    [InlineData(null, "msvsmon.exe", 4026, null, ServiceKind.VsDebugger, ServiceConfidence.Known)]
    [InlineData(null, "node.exe", 9229, null, ServiceKind.NodeInspector, ServiceConfidence.Guess)]
    [InlineData(null, "something.exe", 9092, null, ServiceKind.Kafka, ServiceConfidence.Guess)]
    [InlineData(null, "something.exe", 4242, null, ServiceKind.Unknown, ServiceConfidence.None)]
    public void Recognises_by_image_then_process_then_port(string? image, string? process, int port, int? containerPort, ServiceKind kind, ServiceConfidence confidence)
    {
        var service = ServiceRecognizer.Recognize(image, process, port, containerPort);

        Assert.Equal(kind, service.Kind);
        Assert.Equal(confidence, service.Confidence);
    }

    [Fact]
    public void Image_wins_over_a_conflicting_port()
    {
        var service = ServiceRecognizer.Recognize("redis:7", null, 5432, 5432);

        Assert.Equal(ServiceKind.Redis, service.Kind);
    }

    [Fact]
    public void A_guessed_mysql_port_does_not_claim_one_product()
    {
        Assert.Equal("MySQL / MariaDB", ServiceRecognizer.Recognize(null, "app.exe", 3306).Label);
    }

    [Fact]
    public void Mailpit_web_port_is_labelled_as_its_ui_with_an_http_url()
    {
        var service = ServiceRecognizer.Recognize("axllent/mailpit", null, 18025, 8025);

        Assert.Equal("Mailpit UI", service.Label);
        Assert.Equal(ServiceCategory.Web, service.Category);
        Assert.Equal("http://localhost:18025", service.ConnectionString);
    }

    [Fact]
    public void Tooling_images_are_not_mistaken_for_the_service()
    {
        Assert.Equal(ServiceKind.Unknown, ServiceRecognizer.Recognize("mongo-express", null, 8081, 8081).Kind);
        Assert.Equal(ServiceKind.Unknown, ServiceRecognizer.Recognize("provectuslabs/kafka-ui", null, 8080, 8080).Kind);
    }

    [Fact]
    public void Connection_strings_use_the_host_port_and_carry_no_credentials()
    {
        var service = ServiceRecognizer.Recognize("postgres:16", null, 15432, 5432);

        Assert.Equal("postgresql://localhost:15432", service.ConnectionString);
    }

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("[::]", true)]
    [InlineData("0:0:0:0:0:0:0:0", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("5432->5432/tcp", false)]
    [InlineData(null, false)]
    public void Only_wildcard_addresses_are_all_interfaces(string? address, bool expected)
    {
        Assert.Equal(expected, ServiceRecognizer.IsAllInterfaces(address));
    }

    [Fact]
    public void Docker_parse_keeps_the_bind_address_and_prefers_the_wildcard_one()
    {
        const string json = """
            {"Id":"0123456789abcdef","Names":["/db"],"Image":"postgres:16","State":"running","Status":"Up","Created":1700000000,
             "Ports":[{"IP":"127.0.0.1","PrivatePort":5432,"PublicPort":5432,"Type":"tcp"},
                      {"IP":"0.0.0.0","PrivatePort":6379,"PublicPort":6379,"Type":"tcp"},
                      {"IP":"::","PrivatePort":6379,"PublicPort":6379,"Type":"tcp"}]}
            """;

        using var document = JsonDocument.Parse(json);
        var ports = DockerClient.Parse(document.RootElement).Ports;

        Assert.Equal("127.0.0.1", ports.Single(p => p.HostPort == 5432).BindAddress);
        Assert.Equal("0.0.0.0", ports.Single(p => p.HostPort == 6379).BindAddress);
    }
}

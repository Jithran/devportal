using System.Net;

namespace DevPortal.Discovery;

public enum ServiceKind
{
    Unknown, MariaDb, MySql, Postgres, SqlServer, Redis, Dragonfly, Memcached, MongoDb, Kafka, RabbitMq,
    Elasticsearch, Mailpit, Smtp, NodeInspector, JavaDebug, VsDebugger,
}

public enum ServiceCategory { Unknown, Web, Database, Cache, Queue, Search, Mail, Debug }

/// <summary>How the service was identified. A port number alone is a guess: dev ports get reused.</summary>
public enum ServiceConfidence { None, Guess, Known }

/// <param name="ConnectionString">Never holds credentials; the user adds their own.</param>
public sealed record ServiceInfo(
    ServiceKind Kind,
    string Label,
    string Monogram,
    ServiceCategory Category,
    ServiceConfidence Confidence,
    string? ConnectionString);

public static class ServiceRecognizer
{
    private sealed record Definition(
        ServiceKind Kind,
        string Label,
        string Monogram,
        ServiceCategory Category,
        string Template,
        string[] Images,
        string[] Processes,
        int[] Ports,
        int[]? WebPorts = null);

    private static readonly Definition[] Definitions =
    [
        new(ServiceKind.MariaDb, "MariaDB", "MDB", ServiceCategory.Database, "mysql://localhost:{port}", ["mariadb"], ["mariadbd", "mariadbd-safe"], []),
        new(ServiceKind.MySql, "MySQL", "SQL", ServiceCategory.Database, "mysql://localhost:{port}", ["mysql", "percona"], ["mysqld"], [3306]),
        new(ServiceKind.Postgres, "PostgreSQL", "PG", ServiceCategory.Database, "postgresql://localhost:{port}", ["postgres", "postgis", "pgvector", "timescaledb"], ["postgres"], [5432]),
        new(ServiceKind.SqlServer, "SQL Server", "MS", ServiceCategory.Database, "sqlserver://localhost:{port}", ["mssql", "sqlserver"], ["sqlservr"], [1433]),
        new(ServiceKind.MongoDb, "MongoDB", "MGO", ServiceCategory.Database, "mongodb://localhost:{port}", ["mongo"], ["mongod"], [27017]),
        new(ServiceKind.Dragonfly, "Dragonfly", "DF", ServiceCategory.Cache, "redis://localhost:{port}", ["dragonfly", "dragonflydb"], ["dragonfly"], []),
        new(ServiceKind.Redis, "Redis", "RDS", ServiceCategory.Cache, "redis://localhost:{port}", ["redis", "valkey", "keydb"], ["redis-server", "valkey-server", "keydb-server"], [6379]),
        new(ServiceKind.Memcached, "Memcached", "MC", ServiceCategory.Cache, "localhost:{port}", ["memcached"], ["memcached"], [11211]),
        new(ServiceKind.Kafka, "Kafka", "KF", ServiceCategory.Queue, "localhost:{port}", ["kafka", "redpanda"], [], [9092]),
        new(ServiceKind.RabbitMq, "RabbitMQ", "RMQ", ServiceCategory.Queue, "amqp://localhost:{port}", ["rabbitmq"], ["beam.smp"], [5672], [15672]),
        new(ServiceKind.Elasticsearch, "Elasticsearch", "ES", ServiceCategory.Search, "http://localhost:{port}", ["elasticsearch", "opensearch"], [], [9200, 9300]),
        new(ServiceKind.Mailpit, "Mailpit", "SMTP", ServiceCategory.Mail, "smtp://localhost:{port}", ["mailpit", "mailhog", "mailcatcher"], ["mailpit", "mailhog"], [], [8025]),
        new(ServiceKind.Smtp, "SMTP", "SMTP", ServiceCategory.Mail, "smtp://localhost:{port}", [], [], [1025]),
        new(ServiceKind.NodeInspector, "Node inspector", "DBG", ServiceCategory.Debug, "localhost:{port}", [], [], [9229]),
        new(ServiceKind.JavaDebug, "Java debugger", "DBG", ServiceCategory.Debug, "localhost:{port}", [], [], [5005]),
        new(ServiceKind.VsDebugger, "VS remote debugger", "DBG", ServiceCategory.Debug, "localhost:{port}", [], ["msvsmon"], [4022, 4024, 4026]),
    ];

    // Images that carry a service name but are a UI or exporter for it, not the service itself.
    private static readonly string[] Tooling = ["exporter", "express", "admin", "-ui", "ui-"];

    public static readonly ServiceInfo Unknown = new(ServiceKind.Unknown, "Unknown", "?", ServiceCategory.Unknown, ServiceConfidence.None, null);

    /// <summary>Image and process name win over the port number. <paramref name="containerPort"/> is the port inside the container, when there is one.</summary>
    public static ServiceInfo Recognize(string? image, string? processName, int? port, int? containerPort = null)
    {
        var imageName = ImageName(image);
        var process = Path.GetFileNameWithoutExtension(processName ?? "").ToLowerInvariant();
        var ports = new[] { containerPort, port }.OfType<int>().ToArray();

        var byImage = imageName.Length == 0 || Tooling.Any(imageName.Contains) ? null : Definitions.FirstOrDefault(d => d.Images.Any(imageName.StartsWith));
        var byProcess = process.Length == 0 ? null : Definitions.FirstOrDefault(d => d.Processes.Contains(process));

        var identified = byImage ?? byProcess;
        if (identified is not null)
            return Build(identified, ServiceConfidence.Known, port, ports);

        var byPort = Definitions.FirstOrDefault(d => ports.Any(d.Ports.Contains));
        return byPort is null || port is null ? Unknown : Build(byPort, ServiceConfidence.Guess, port, ports);
    }

    /// <summary>Wildcard binds (0.0.0.0, ::, [::]) make a listener reachable from the network.</summary>
    public static bool IsAllInterfaces(string? address) =>
        IPAddress.TryParse(address?.Trim('[', ']'), out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any));

    private static ServiceInfo Build(Definition definition, ServiceConfidence confidence, int? port, int[] ports)
    {
        var isWeb = definition.WebPorts is { } web && ports.Any(web.Contains);
        var label = isWeb ? $"{definition.Label} UI"
            : definition is { Kind: ServiceKind.MySql } && confidence == ServiceConfidence.Guess ? "MySQL / MariaDB"
            : definition.Label;
        var connection = port is { } p ? definition.Template.Replace("{port}", p.ToString()) : null;
        if (isWeb && port is { } webPort)
            connection = $"http://localhost:{webPort}";
        return new ServiceInfo(definition.Kind, label, isWeb ? "WEB" : definition.Monogram, isWeb ? ServiceCategory.Web : definition.Category, confidence, connection);
    }

    /// <summary>"ghcr.io/axllent/mailpit:v1.20" → "mailpit"; the registry, owner and tag never decide.</summary>
    private static string ImageName(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
            return "";
        var name = image.Split('@')[0];
        name = name[(name.LastIndexOf('/') + 1)..];
        var tag = name.IndexOf(':');
        return (tag < 0 ? name : name[..tag]).ToLowerInvariant();
    }
}

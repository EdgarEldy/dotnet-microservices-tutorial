using System.Diagnostics;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Identity.API.Tests.TestSupport;

/// <summary>What a bounded read of a Kafka topic saw.</summary>
public sealed record KafkaReadResult(IReadOnlyList<string> Values, string? Match);

/// <summary>
/// Reads identity-api's Kafka topics from the beginning with a throwaway consumer group, the way
/// notification-worker will, and parses the event bodies. Every read is bounded by a timeout.
/// </summary>
public static class KafkaTopicReader
{
    public static async Task CreateTopicsAsync(string bootstrapServers, params string[] topics)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync(topics.Select(topic =>
                new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }));
        }
        catch (CreateTopicsException exception)
            when (exception.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            // Already there: nothing to do.
        }
    }

    /// <summary>
    /// Consumes <paramref name="topic"/> from its first offset until a value satisfies
    /// <paramref name="isMatch"/> or <paramref name="timeout"/> elapses.
    /// </summary>
    public static Task<KafkaReadResult> ReadUntilAsync(
        string bootstrapServers, string topic, Func<string, bool> isMatch, TimeSpan timeout) =>
        Task.Run(() =>
        {
            var config = new ConsumerConfig
            {
                BootstrapServers = bootstrapServers,
                GroupId = $"identity-api-tests-{Guid.NewGuid():N}",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
            };

            using var consumer = new ConsumerBuilder<Ignore, string>(config).Build();
            consumer.Subscribe(topic);

            var values = new List<string>();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                while (stopwatch.Elapsed < timeout)
                {
                    var remaining = timeout - stopwatch.Elapsed;
                    var record = consumer.Consume(remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500));
                    if (record?.Message?.Value is not { } value)
                    {
                        continue;
                    }

                    values.Add(value);
                    if (isMatch(value))
                    {
                        return new KafkaReadResult(values, value);
                    }
                }
            }
            finally
            {
                consumer.Close();
            }

            return new KafkaReadResult(values, null);
        });

    /// <summary>True when the event body carries <paramref name="email"/> as its Email field.</summary>
    public static bool HasEmail(string value, string email) =>
        string.Equals(GetString(value, "email"), email, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a string field of the event, whether the body is the raw message or a MassTransit envelope.</summary>
    public static string? GetString(string value, string propertyName) =>
        TryGetProperty(value, propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    public static int GetInt32(string value, string propertyName) =>
        TryGetProperty(value, propertyName, out var property)
            ? property.GetInt32()
            : throw new InvalidOperationException($"The event has no '{propertyName}' field: {value}");

    private static bool TryGetProperty(string value, string propertyName, out JsonElement property)
    {
        property = default;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(value);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryGetPropertyIgnoreCase(root, "message", out var envelopeMessage) && envelopeMessage.ValueKind == JsonValueKind.Object)
        {
            root = envelopeMessage;
        }

        return TryGetPropertyIgnoreCase(root, propertyName, out property);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement property)
    {
        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }
}

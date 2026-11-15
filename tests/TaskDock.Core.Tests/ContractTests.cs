using System.Text.Json;
using System.Text.Json.Nodes;
using TaskDock.Core.Protocol;

namespace TaskDock.Core.Tests;

/// <summary>Every golden frame in protocol/samples must survive the C# DTOs unchanged.</summary>
public class ContractTests
{
    public static TheoryData<string> Samples() => new(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples"), "*.json").Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Sample_round_trips_through_the_dtos(string file)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", file));
        var env = Wire.Parse(text)!;
        Assert.Equal(Wire.Version, env.V);

        object? body = env.Type switch
        {
            "hello" => env.Body<HelloBody>(),
            "welcome" => env.Body<WelcomeBody>(),
            "state" => env.Body<StateBody>(),
            "command" => env.Body<CommandBody>(),
            "result" => env.Body<ResultBody>(),
            "bye" => env.Body<ByeBody>(),
            "heartbeat" => env.Body<HeartbeatBody>(),
            _ => throw new Xunit.Sdk.XunitException($"unknown type {env.Type}"),
        };
        Assert.NotNull(body);

        var again = JsonSerializer.SerializeToNode(env with { Body = JsonSerializer.SerializeToElement(body, body.GetType(), Wire.Json) }, Wire.Json);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), again), $"{file} changed:\n{again}");
    }

    [Fact]
    public void There_are_samples() => Assert.True(Samples().Count >= 8);
}

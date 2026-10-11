using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AriaGui.Browser.Contracts;
using AriaGui.Core.Downloads;

namespace AriaGui.Browser.Contracts.Tests;

public sealed class ContractTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "v1.json");
    public static IEnumerable<object[]> Fixtures()
    {
        using var cases = JsonDocument.Parse(File.ReadAllBytes(FixturePath));
        foreach (var item in cases.RootElement.EnumerateArray()) yield return [item.GetProperty("name").GetString()!, item.GetRawText()];
    }
    [Theory, MemberData(nameof(Fixtures))]
    public void SharedFixturesParseAndRoundTrip(string name, string fixture)
    {
        using var document = JsonDocument.Parse(fixture); var item = document.RootElement;
        var bytes = Encoding.UTF8.GetBytes(item.TryGetProperty("raw", out var raw) ? raw.GetString()! : item.GetProperty("json").GetRawText());
        var valid = item.GetProperty("valid").GetBoolean();
        if (item.GetProperty("kind").GetString() == "request")
        {
            var parsed = ContractJson.ParseRequest(bytes); Assert.Equal(valid, parsed.Success);
            if (!valid) { Assert.Equal(Enum.Parse<RejectionReason>(item.GetProperty("error").GetString()!), parsed.Error); return; }
            var serialized = ContractJson.Serialize(parsed.Value!); var again = ContractJson.ParseRequest(serialized);
            Assert.True(again.Success); Assert.Equal(serialized, ContractJson.Serialize(again.Value!));
            if (item.TryGetProperty("sourceUrl", out var source)) Assert.Equal(source.GetString(), ((PublicGetPayload)parsed.Value!.Payload).SourceUrl);
        }
        else
        {
            var parsed = ContractJson.ParseResponse(bytes); Assert.Equal(valid, parsed.Success);
            if (!valid) { Assert.Equal(Enum.Parse<RejectionReason>(item.GetProperty("error").GetString()!), parsed.Error); return; }
            var serialized = ContractJson.Serialize(parsed.Value!);
            Assert.Equal(serialized, ContractJson.Serialize(ContractJson.ParseResponse(serialized).Value!));
        }
        Assert.False(string.IsNullOrEmpty(name));
    }
    private static JsonObject GetRequest()
    {
        var fixtures = JsonNode.Parse(File.ReadAllBytes(FixturePath))!.AsArray();
        return fixtures[1]!["json"]!.DeepClone().AsObject();
    }
    private static ContractParseResult<RequestEnvelope> Parse(JsonObject value) => ContractJson.ParseRequest(Encoding.UTF8.GetBytes(value.ToJsonString()));
    [Theory]
    [InlineData("https://example.test/a\r\nInjected: secret")]
    [InlineData("https://example.test/a\0")]
    [InlineData("https://@example.test/file")]
    [InlineData("https://user:pass@example.test/file")]
    [InlineData("file:///C:/secret")]
    [InlineData("data:text/plain,hello")]
    [InlineData("blob:https://example.test/id")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://example.test/?sig=a%2")]
    [InlineData("https://example.test/?sig=%GG")]
    [InlineData("https://example.test/a%00")]
    [InlineData("https://example.test/a%0D%0A")]
    [InlineData("https:\\example.test/file")]
    [InlineData("https://example.test:99999/file")]
    public void C03UnsafeUrlsRejected(string url)
    {
        var request = GetRequest(); request["payload"]!["sourceUrl"] = url;
        var result = Parse(request); Assert.False(result.Success); Assert.Equal(RejectionReason.UnsupportedUrl, result.Error);
        Assert.NotNull(result.CorrelationId);
    }
    [Theory]
    [InlineData("http://127.0.0.1/file")]
    [InlineData("http://10.0.0.1/file")]
    [InlineData("http://169.254.1.2/file")]
    [InlineData("http://[::1]/file")]
    [InlineData("http://[fe80::1]/file")]
    [InlineData("http://[fd00::1]/file")]
    public void NetworkAdmissionBelongsToAuthoritativeIntakeNotSyntaxParser(string url)
    {
        // Package 1 parses the wire shape only. Package 2 MUST reject these before durable acceptance.
        // No DNS lookup, HTTP request or engine mutation occurs here.
        var request = GetRequest(); request["payload"]!["sourceUrl"] = url;
        Assert.True(Parse(request).Success);
    }
    [Theory]
    [InlineData("../file.zip")]
    [InlineData("a\\b")]
    [InlineData("C:\\file")]
    [InlineData("file:secret")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM9.zip")]
    [InlineData("LPT².zip")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("line\nname")]
    [InlineData("question?.zip")]
    public void C04InvalidBasenamesUseSharedPolicy(string name)
    {
        Assert.False(WindowsFileNamePolicy.IsValid(name));
        var request = GetRequest(); request["payload"]!["suggestedFileName"] = name;
        Assert.Equal(RejectionReason.InvalidFilename, Parse(request).Error);
    }
    [Fact]
    public void C04FilenameLengthAndUnicode()
    {
        Assert.True(WindowsFileNamePolicy.IsValid(new string('λ', 240)));
        var request = GetRequest(); request["payload"]!["suggestedFileName"] = new string('λ', 240); Assert.True(Parse(request).Success);
        request["payload"]!["suggestedFileName"] = new string('λ', 241); Assert.Equal(RejectionReason.InvalidFilename, Parse(request).Error);
    }
    [Fact]
    public void C02InvalidUtf8JsonBomAndDepthCannotSupplyCorrelation()
    {
        foreach (var bytes in new[] { new byte[] { 0xc0, 0xaf }, Encoding.UTF8.GetBytes("{broken"), Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{}")).ToArray(),
            Encoding.UTF8.GetBytes(new string('[', 9) + "0" + new string(']', 9)), Array.Empty<byte>(), new byte[65537] })
        { var result = ContractJson.ParseRequest(bytes); Assert.False(result.Success); Assert.Null(result.CorrelationId); }
    }
    [Fact]
    public void C02DuplicateNestedPropertiesCannotSupplyCorrelation()
    {
        var raw = GetRequest().ToJsonString().Replace("\"major\":1", "\"major\":1,\"major\":1", StringComparison.Ordinal);
        var result = ContractJson.ParseRequest(Encoding.UTF8.GetBytes(raw)); Assert.False(result.Success); Assert.Null(result.CorrelationId);
    }
    [Theory]
    [InlineData("headers")]
    [InlineData("directory")]
    [InlineData("callerIdentity")]
    [InlineData("rpcOptions")]
    [InlineData("cookie")]
    public void C05UnknownFieldsReject(string field)
    {
        var request = GetRequest(); request["payload"]![field] = "SYNTHETIC_SECRET";
        Assert.Equal(RejectionReason.InvalidRequest, Parse(request).Error);
    }
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("9D72E0BE-8CDD-41C2-9B20-A78E6060DF41")]
    [InlineData("9d72e0be8cdd41c29b20a78e6060df41")]
    [InlineData("not-a-uuid")]
    public void CanonicalNonzeroUuidRequired(string id)
    { var request = GetRequest(); request["requestId"] = id; var result = Parse(request); Assert.False(result.Success); Assert.Null(result.CorrelationId); }
    [Fact]
    public void C01UrlLimitCountsUtf8BytesAndPreservesSignedQuery()
    {
        var request = GetRequest(); const string prefix = "https://example.test/";
        var count = (ProtocolLimits.MaximumUrlBytes - Encoding.UTF8.GetByteCount(prefix)) / 2;
        request["payload"]!["sourceUrl"] = prefix + new string('λ', count);
        Assert.True(Parse(request).Success);
        request["payload"]!["sourceUrl"] = prefix + new string('λ', count + 1);
        Assert.Equal(RejectionReason.InvalidRequest, Parse(request).Error);
    }
    [Fact]
    public void OutgoingInvalidEnvelopeRejectedBeforeFraming()
    {
        var valid = Parse(GetRequest()).Value!;
        Assert.Throws<ArgumentException>(() => ContractJson.Serialize(valid with { Action = BrowserAction.Hello }));
        Assert.Throws<ArgumentException>(() => ContractJson.Serialize(new ResponseEnvelope(new(1, 0), Guid.NewGuid(), new(ResultCode.Accepted))));
    }
    [Theory]
    [InlineData("createdAt", "2026-02-30T12:00:00Z")]
    [InlineData("createdAt", "2026-10-11T12:00:00+00:00")]
    [InlineData("createdAt", "2026-10-11T12:00:00.12345678Z")]
    [InlineData("trigger", "automatic")]
    public void MalformedTimestampOrUnsupportedTriggerRejects(string field, string value)
    { var request = GetRequest(); request["payload"]![field] = value; Assert.Equal(RejectionReason.InvalidRequest, Parse(request).Error); }
    [Fact]
    public void NullOptionalNameIsNotOmittedName()
    { var request = GetRequest(); request["payload"]!["suggestedFileName"] = null; Assert.Equal(RejectionReason.InvalidRequest, Parse(request).Error); }
    [Fact]
    public void ResponseShapesAndEnumsAreClosed()
    {
        foreach (var result in new[] { "{\"code\":\"accepted\",\"jobId\":\"4f8cbb4a-9b09-42f1-a21e-945718b95066\",\"state\":\"engine_uncertain\",\"duplicate\":1}",
            "{\"code\":\"processing\",\"retryAfterMs\":10001}", "{\"code\":\"rejected\",\"reason\":\"raw_failure\"}",
            "{\"code\":\"ready\",\"appVersion\":\"1.0.0\",\"capabilities\":[\"addPublicGet\",\"addPublicGet\"],\"maxMessageBytes\":65536}" })
        {
            var json = "{\"protocol\":{\"major\":1,\"minor\":0},\"requestId\":\"e204774d-08ba-4a09-96c9-9e64f2b31ce3\",\"result\":" + result + "}";
            Assert.Equal(RejectionReason.InvalidRequest, ContractJson.ParseResponse(Encoding.UTF8.GetBytes(json)).Error);
        }
    }
    [Fact]
    public void EscapedDuplicateAndInvalidSurrogateFailSafely()
    {
        var json = GetRequest().ToJsonString().Replace("\"major\":1", "\"major\":1,\"ma\\u006aor\":1", StringComparison.Ordinal);
        var duplicate = ContractJson.ParseRequest(Encoding.UTF8.GetBytes(json)); Assert.False(duplicate.Success); Assert.Null(duplicate.CorrelationId);
        json = GetRequest().ToJsonString();
        var node = JsonNode.Parse(json)!; node["payload"]!["suggestedFileName"] = "SURROGATE";
        json = node.ToJsonString().Replace("SURROGATE", "\\uD800", StringComparison.Ordinal);
        Assert.False(ContractJson.ParseRequest(Encoding.UTF8.GetBytes(json)).Success);
    }
}

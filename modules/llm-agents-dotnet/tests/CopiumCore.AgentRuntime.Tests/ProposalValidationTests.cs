using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopiumCore.AgentRuntime;
using CopiumCore.Application.Graph;
using Xunit;

namespace CopiumCore.AgentRuntime.Tests;

public class ProposalValidationTests
{
    private static JsonObject Candidate() => JsonNode.Parse("""
        {"outcome":"proposalCreated","summary":"Change group", "proposalIds":[],
         "evidence":[{"objectId":"app-1","version":"a1"},{"objectId":"decision-1","version":"d1"}],
         "proposalCandidate":{"operation":"setAttribute","objectId":"app-1","expectedVersion":"a1",
         "field":"groupCode","newValue":"new-group","decisionId":"decision-1","decisionVersion":"d1"}}
        """)!.AsObject();

    private static ConcurrentBag<GraphObject> Reads(string target = """
        {"groupCode":"old-group","decisionId":"decision-1"}
        """, string decision = """
        {"groupCode":"new-group","approved":true,"applicationId":"app-1"}
        """, string targetType = "Application", string decisionType = "Decision") =>
        new([new GraphObject("app-1", targetType, "a1", JsonSerializer.Deserialize<JsonElement>(target)),
             new GraphObject("decision-1", decisionType, "d1", JsonSerializer.Deserialize<JsonElement>(decision))]);

    [Fact]
    public void Valid_candidate_produces_runtime_owned_proposal_and_evidence()
    {
        Assert.True(AgentExecutor.TryValidate(Candidate().ToJsonString(), Reads(), out var result));
        var proposal = Assert.IsType<PreparedProposal>(result!.Proposal);
        Assert.True(Guid.TryParseExact(proposal.Id, "N", out _));
        Assert.Equal(new[] { proposal.Id }, result.ProposalIds);
        Assert.Equal("setAttribute", proposal.Operation);
        Assert.Equal("app-1", proposal.ObjectId);
        Assert.Equal("a1", proposal.ExpectedVersion);
        Assert.Equal("groupCode", proposal.Field);
        Assert.Equal("old-group", proposal.Before.GetString());
        Assert.Equal("new-group", proposal.After);
        SourceRef[] expected = [new("app-1", "a1", "groupCode"), new("app-1", "a1", "decisionId"),
            new("decision-1", "d1", "groupCode"), new("decision-1", "d1", "approved"),
            new("decision-1", "d1", "applicationId")];
        Assert.Equal(expected, proposal.Evidence);
        Assert.Equal(expected, result.Evidence);
    }

    [Theory]
    [InlineData("newValue", "forged-group")]
    [InlineData("objectId", "other-app")]
    [InlineData("expectedVersion", "a2")]
    [InlineData("decisionId", "other-decision")]
    [InlineData("decisionVersion", "d2")]
    [InlineData("operation", "delete")]
    [InlineData("field", "approved")]
    public void Rejects_forged_candidate_fields(string field, string value)
    {
        var response = Candidate();
        response["proposalCandidate"]![field] = value;
        Assert.False(AgentExecutor.TryValidate(response.ToJsonString(), Reads(), out var result));
        Assert.Null(result);
    }

    [Theory]
    [InlineData("{\"groupCode\":\"new-group\",\"approved\":false,\"applicationId\":\"app-1\"}")]
    [InlineData("{\"groupCode\":\"new-group\",\"approved\":\"true\",\"applicationId\":\"app-1\"}")]
    [InlineData("{\"groupCode\":\"new-group\",\"applicationId\":\"app-1\"}")]
    [InlineData("{\"groupCode\":\"other\",\"approved\":true,\"applicationId\":\"app-1\"}")]
    [InlineData("{\"groupCode\":\"new-group\",\"approved\":true,\"applicationId\":\"other\"}")]
    public void Rejects_unapproved_or_mismatched_decision(string decision)
    {
        Assert.False(AgentExecutor.TryValidate(Candidate().ToJsonString(), Reads(decision: decision), out _));
    }

    [Theory]
    [InlineData("{\"groupCode\":\"old-group\",\"decisionId\":\"other\"}")]
    [InlineData("{\"groupCode\":\"old-group\"}")]
    [InlineData("{\"groupCode\":42,\"decisionId\":\"decision-1\"}")]
    public void Rejects_invalid_target_or_decision_link(string target)
    {
        Assert.False(AgentExecutor.TryValidate(Candidate().ToJsonString(), Reads(target: target), out _));
    }

    [Theory]
    [InlineData("Other", "Decision")]
    [InlineData("Application", "Other")]
    public void Rejects_wrong_object_types(string targetType, string decisionType)
    {
        Assert.False(AgentExecutor.TryValidate(Candidate().ToJsonString(), Reads(targetType: targetType, decisionType: decisionType), out _));
    }

    [Theory]
    [InlineData(0, "version", "a2")]
    [InlineData(1, "version", "d2")]
    [InlineData(0, "objectId", "forged")]
    [InlineData(1, "field", "forged")]
    public void Rejects_forged_evidence(int index, string field, string value)
    {
        var response = Candidate();
        response["evidence"]![index]![field] = value;
        Assert.False(AgentExecutor.TryValidate(response.ToJsonString(), Reads(), out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Requires_evidence_for_both_objects(int omittedIndex)
    {
        var response = Candidate();
        response["evidence"]!.AsArray().RemoveAt(omittedIndex);
        Assert.False(AgentExecutor.TryValidate(response.ToJsonString(), Reads(), out _));
    }

    [Fact]
    public void Rejects_model_supplied_proposal_and_ids()
    {
        var response = Candidate();
        response["proposalIds"] = new JsonArray("forged");
        Assert.False(AgentExecutor.TryValidate(response.ToJsonString(), Reads(), out _));
        response["proposalIds"] = new JsonArray();
        response["proposal"] = JsonNode.Parse("""
            {"id":"forged","operation":"setAttribute","objectId":"app-1","expectedVersion":"a1",
             "field":"groupCode","before":"old-group","after":"new-group","evidence":[]}
            """);
        Assert.False(AgentExecutor.TryValidate(response.ToJsonString(), Reads(), out _));
    }
}

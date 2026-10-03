using Recepcion;
using Xunit;

public class InboxWorkflowTests
{
    [Theory]
    [InlineData("pending", "human")]
    [InlineData("resolved", "closed")]
    [InlineData("snoozed", "human")]
    public void WaitingAndResolvedStatesPauseAgentAndPreserveOwner(string state,string status)
    {
        var row=new Conversation{Status="agent",AssignedTo="doctor-1"};
        InboxWorkflow.SetState(row,state,DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(status,row.Status);Assert.Equal("doctor-1",row.AssignedTo);
        Assert.Equal(state=="snoozed",row.SnoozedUntil.HasValue);
        Assert.True(InboxWorkflow.ReopenOnInbound(row));
        Assert.Equal("open",row.State);Assert.Equal("human",row.Status);Assert.Null(row.SnoozedUntil);
    }
    [Fact]
    public void IncomingMessageDoesNotInterruptAnActiveAgent()
    {
        var row=new Conversation{State="open",Status="agent"};
        Assert.False(InboxWorkflow.ReopenOnInbound(row));Assert.Equal("agent",row.Status);
    }
    [Fact]
    public void InvalidSnoozeDoesNotMutateConversation()
    {
        var row=new Conversation();
        Assert.Throws<ArgumentException>(()=>InboxWorkflow.SetState(row,"snoozed",DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal("open",row.State);
        Assert.Throws<ArgumentException>(()=>InboxWorkflow.SetState(row,"snoozed",DateTimeOffset.UtcNow.AddDays(31)));
    }
    [Fact]
    public void LabelsAreNormalizedAndBounded()
    {
        Assert.Equal("cita, receta",InboxWorkflow.Labels(" CITA, receta, cita, "));
        Assert.Throws<ArgumentException>(()=>InboxWorkflow.Labels(new string('a',41)));
    }
}

using Recepcion;
using Xunit;
public class ProductivityTests
{
    [Fact] public void MacroAddsLabelsWithoutRemovingExistingAndTakesOwnership()
    {
        var row=new Conversation{Labels="cita",Status="agent"};
        var macro=ProductivityEndpoints.CreateMacro(new("Revisión","pending","high","CITA, doctor","Revisar consulta",true));
        ProductivityEndpoints.Apply(macro,row,"dev-doctor");
        Assert.Equal("cita, doctor",row.Labels);Assert.Equal("pending",row.State);Assert.Equal("human",row.Status);Assert.Equal("dev-doctor",row.AssignedTo);
    }
    [Fact] public void InvalidMergedLabelsDoNotPartiallyChangeState()
    {
        var row=new Conversation{Labels=string.Join(',',Enumerable.Range(1,10)),State="open"};
        var macro=ProductivityEndpoints.CreateMacro(new("Revisión","resolved","high","extra",null));
        Assert.Throws<ArgumentException>(()=>ProductivityEndpoints.Apply(macro,row,"user"));Assert.Equal("open",row.State);Assert.Equal("normal",row.Priority);
    }
    [Theory][InlineData("agent")][InlineData("snoozed")][InlineData("invalid")]
    public void MacroCannotEnableAgentOrUseUnsupportedState(string state)=>Assert.Throws<ArgumentException>(()=>ProductivityEndpoints.CreateMacro(new("Test",state,null,null,null)));
    [Fact] public void EmptyMacrosAreRejected()=>Assert.Throws<ArgumentException>(()=>ProductivityEndpoints.CreateMacro(new("Test",null,null,"",null)));
    [Fact] public void ViewsAllowOnlySupportedFilters()
    {
        Assert.Equal("cita",ProductivityEndpoints.ValidateFilters(new(Label:" CITA ")).Label);
        Assert.Throws<ArgumentException>(()=>ProductivityEndpoints.ValidateFilters(new(Assignment:"other-user")));
        Assert.Throws<ArgumentException>(()=>ProductivityEndpoints.ValidateFilters(new(ChannelId:"not-guid")));
        Assert.Throws<ArgumentException>(()=>ProductivityEndpoints.ValidateFilters(new(Label:"one,two")));
    }
}

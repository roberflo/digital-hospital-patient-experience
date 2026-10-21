using System.Text.Json;
using System.Text.Json.Nodes;
using Recepcion;
using Recepcion.Integrations;
using Xunit;
public class ReminderRulesTests
{
    [Theory]
    [InlineData("2026-10-05T16:30:00Z","America/El_Salvador","2026-10-04T15:00:00Z")]
    [InlineData("2026-10-05T06:30:00Z","America/El_Salvador","2026-10-04T15:00:00Z")]
    [InlineData("2026-03-08T16:00:00Z","America/New_York","2026-03-07T14:00:00Z")]
    public void DayBeforeUsesNineAmHospitalTime(string starts,string zone,string due)
    {
        Assert.Equal(DateTimeOffset.Parse(due),ReminderRules.Due(DateTimeOffset.Parse(starts),"day_before",zone));
        Assert.Equal(DateTimeOffset.Parse(starts).AddHours(-1),ReminderRules.Due(DateTimeOffset.Parse(starts),"hour_before",zone));
    }
    [Theory][InlineData(" baja ","off")][InlineData("ACTIVAR RECORDATORIOS","on")][InlineData("Quiero cancelar mi cita",null)]
    public void ConsentCommandsDoNotMistakeAppointmentCancellation(string text,string? expected)=>Assert.Equal(expected,ReminderRules.ConsentCommand(text));

    // Criterios 7 y 10: what a tap leaves for the desk is filed where the desk looks, not among CRM updates.
    [Theory]
    [InlineData("appointment_confirmed","appointment")]
    [InlineData("appointment_confirmation_rejected","review")]
    [InlineData("appointment_confirmation_unverified","review")]
    [InlineData("reminder_change_request","review")]
    public void ReminderReplyNotesAreFiledForTheDesk(string kind,string category)=>Assert.Equal(category,ActivityFeed.Category(kind));

    // Criterio 1: the file in the repo is what is submitted to Meta; Meta's answer adds the status.
    [Fact] public void DayBeforeV4TemplateInRepoIsValid()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!Directory.Exists(Path.Combine(root.FullName,"integrations","kapso","templates")))root=root.Parent;
        Assert.NotNull(root);
        var template=JsonNode.Parse(File.ReadAllText(Path.Combine(root!.FullName,"integrations","kapso","templates","dia_anterior_v4.json")))!.AsObject();
        Assert.Equal("recepcion_cita_dia_anterior_v4",(string?)template["name"]);
        template["status"]="APPROVED";
        Assert.True(KapsoClient.ValidReminderTemplate(JsonSerializer.SerializeToElement(template),"day_before"));
        var buttons=template["components"]!.AsArray().Single(c=>(string?)c!["type"]=="BUTTONS")!["buttons"]!.AsArray();
        Assert.All(buttons,b=>Assert.True(((string)b!["text"]!).Length<=20));
    }

    static JsonElement Template(string body="Cita en {{hospital}} el {{fecha}} a las {{hora}}. BAJA",string[]? buttons=null,string buttonType="QUICK_REPLY",bool header=false,string? footer=null)
    {
        var components=new List<object>();
        if(header)components.Add(new{type="HEADER",format="TEXT",text="Recordatorio"});
        components.Add(new{type="BODY",text=body});
        if(footer is not null)components.Add(new{type="FOOTER",text=footer});
        if(buttons is not null)components.Add(new{type="BUTTONS",buttons=buttons.Select((t,i)=>i==buttons.Length-1&&buttonType!="QUICK_REPLY"?(object)new{type=buttonType,text=t,url="https://example.invalid"}:new{type="QUICK_REPLY",text=t}).ToArray()});
        return JsonSerializer.SerializeToElement(new{status="APPROVED",category="UTILITY",parameter_format="NAMED",components});
    }
    static readonly string[] V4=["Confirmar","Cambiar fecha","Cancelar"];
    const string NoBaja="Cita en {{hospital}} el {{fecha}} a las {{hora}}.";
    const string Footer="Para dejar de recibir recordatorios escribe BAJA.";
    public static TheoryData<string,JsonElement,string,bool> Shapes()=>new()
    {
        {"v1 hour",Template(),"hour_before",true},
        {"v1 day",Template(),"day_before",false},
        {"v4 day",Template(buttons:V4),"day_before",true},
        {"v4 hour",Template(buttons:V4),"hour_before",false},
        {"v4 day, BAJA in the footer",Template(body:NoBaja,footer:Footer,buttons:V4),"day_before",true},
        {"v4 day, footer without BAJA",Template(body:NoBaja,footer:"Gracias",buttons:V4),"day_before",false},
        {"v4 day, BAJA nowhere",Template(body:NoBaja,buttons:V4),"day_before",false},
        {"footer without buttons",Template(body:NoBaja,footer:Footer),"day_before",false},
        {"v1 hour with footer",Template(body:NoBaja,footer:Footer),"hour_before",false},
        {"v2 labels",Template(buttons:["Confirmo","Cambiar fecha","Cancelar cita"]),"day_before",false},
        {"fourth button",Template(buttons:[..V4,"Otro"]),"day_before",false},
        {"url button",Template(buttons:V4,buttonType:"URL"),"day_before",false},
        {"phone button",Template(buttons:V4,buttonType:"PHONE_NUMBER"),"day_before",false},
        {"other label",Template(buttons:["Sí","Cambiar fecha","Cancelar"]),"day_before",false},
        {"doctor parameter",Template(body:"Cita con {{doctor}} en {{hospital}} el {{fecha}} a las {{hora}}. BAJA",buttons:V4),"day_before",false},
        {"header",Template(buttons:V4,header:true),"day_before",false},
        {"other window",Template(buttons:V4),"otra",false},
        {"other window v1",Template(),"otra",false},
    };
    // Criterio 2: the shape that may be sent depends on the window.
    [Theory][MemberData(nameof(Shapes))]
    public void TemplateShapeMatchesItsWindow(string _,JsonElement template,string window,bool expected)=>Assert.Equal(expected,KapsoClient.ValidReminderTemplate(template,window));
}

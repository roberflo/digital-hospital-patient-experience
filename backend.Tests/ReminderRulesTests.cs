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

    // Criterios 42, 43 y 55 (S5): the owner's approved text, literally, with its line breaks and bold; B9 closes the hour before.
    const string DayBeforeV5Text="Hola, mañana tienes cita:\n\n*{{hospital}}*\n{{fecha}}, {{hora}}\n\n¿Nos confirmas tu asistencia?";
    const string HourBeforeV2Text="Hola, tu cita es en una hora:\n\n*{{hospital}}*\n{{fecha}}, {{hora}}\n\nTe esperamos.";
    const string GeneralDayV2Text="Hola, mañana tiene cita:\n\n*{{hospital}}*\n{{fecha}}, {{hora}}\n\nPara cambiar la fecha llame al {{telefono}}.\nSi no podrá asistir, use el botón para cancelarla.";
    const string GeneralHourV2Text="Hola, su cita es en una hora:\n\n*{{hospital}}*\n{{fecha}}, {{hora}}\n\nSi necesita hablar con su clínica, llame al {{telefono}}. Le esperamos.";
    [Theory]
    [InlineData("dia_anterior_v5.json","recepcion_cita_dia_anterior_v5",DayBeforeV5Text,"day_before","hour_before","Confirmar|Cambiar fecha|Cancelar|No más recordatorios")]
    [InlineData("una_hora_v2.json","recepcion_cita_una_hora_v2",HourBeforeV2Text,"hour_before","day_before","Necesito reprogramar|Cancelar cita|No más recordatorios")]
    [InlineData("general_dia_anterior_v2.json","recepcion_cita_general_dia_anterior_v2",GeneralDayV2Text,KapsoClient.GeneralDayWindow,"day_before","Cancelar cita|No más recordatorios")]
    [InlineData("general_una_hora_v2.json","recepcion_cita_general_una_hora_v2",GeneralHourV2Text,KapsoClient.GeneralHourWindow,"hour_before","No más recordatorios")]
    public void S5TemplateInRepoIsTheApprovedTextAndValidOnlyForItsWindow(string file,string name,string body,string window,string otherWindow,string buttons)
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!Directory.Exists(Path.Combine(root.FullName,"integrations","kapso","templates")))root=root.Parent;
        var template=JsonNode.Parse(File.ReadAllText(Path.Combine(root!.FullName,"integrations","kapso","templates",file)))!.AsObject();
        Assert.Equal((name,"es","UTILITY","NAMED"),((string?)template["name"],(string?)template["language"],(string?)template["category"],(string?)template["parameter_format"]));
        var components=template["components"]!.AsArray();
        Assert.Equal(["BODY","BUTTONS"],components.Select(c=>(string?)c!["type"]));
        Assert.Equal(body,(string?)components[0]!["text"]);
        var labels=components[1]!["buttons"]!.AsArray().Select(b=>((string?)b!["type"],(string?)b["text"])).ToArray();
        Assert.Equal(buttons.Split('|').Select(t=>((string?)"QUICK_REPLY",(string?)t)),labels);
        Assert.All(labels,l=>Assert.True(l.Item2!.Length<=20));
        Assert.DoesNotContain("BAJA",body); // the button is the way out (B8)
        template["status"]="APPROVED"; var json=JsonSerializer.SerializeToElement(template);
        Assert.True(KapsoClient.ValidReminderTemplate(json,window));
        Assert.False(KapsoClient.ValidReminderTemplate(json,otherWindow));
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
    // S5 (criterio 44): the owner's buttons; «No más recordatorios» stands in for the written BAJA (INV-10).
    static readonly string[] V5=[..V4,"No más recordatorios"], V2=["Necesito reprogramar","Cancelar cita","No más recordatorios"];
    const string NoBaja="Cita en {{hospital}} el {{fecha}} a las {{hora}}.";
    const string GeneralNoBaja="Cita en {{hospital}} el {{fecha}} a las {{hora}}. Llame al {{telefono}}.";
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
        {"v5 day",Template(body:NoBaja,buttons:V5),"day_before",true},
        {"v5 day, BAJA in the footer too",Template(body:NoBaja,footer:Footer,buttons:V5),"day_before",true},
        {"v5 day, stop button misspelled",Template(body:NoBaja,buttons:[..V4,"No mas recordatorios"]),"day_before",false},
        {"v5 day, stop button first",Template(body:NoBaja,buttons:["No más recordatorios",..V4]),"day_before",false},
        {"v5 day, fifth button",Template(body:NoBaja,buttons:[..V5,"Otro"]),"day_before",false},
        {"v5 day, stop as a url",Template(body:NoBaja,buttons:V5,buttonType:"URL"),"day_before",false},
        {"v5 day, header",Template(body:NoBaja,buttons:V5,header:true),"day_before",false},
        {"v5 day, doctor parameter",Template(body:"Cita con {{doctor}} en {{hospital}} el {{fecha}} a las {{hora}}.",buttons:V5),"day_before",false},
        {"v5 in the hour",Template(body:NoBaja,buttons:V5),"hour_before",false},
        {"v2 hour",Template(body:NoBaja,buttons:V2),"hour_before",true},
        {"v2 in the day",Template(body:NoBaja,buttons:V2),"day_before",false},
        {"v2 hour, stop button missing and no BAJA",Template(body:NoBaja,buttons:["Necesito reprogramar","Cancelar cita"]),"hour_before",false},
        {"v2 hour, stop button elsewhere",Template(body:NoBaja,buttons:["No más recordatorios","Necesito reprogramar","Cancelar cita"]),"hour_before",false},
        {"v2 hour, phone button",Template(body:NoBaja,buttons:V2,buttonType:"PHONE_NUMBER"),"hour_before",false},
        {"v2 hour, header",Template(body:NoBaja,buttons:V2,header:true),"hour_before",false},
        {"v2 hour, footer",Template(body:NoBaja,footer:Footer,buttons:V2),"hour_before",false},
        {"v2 hour, fourth button",Template(body:NoBaja,buttons:[..V2,"Otro"]),"hour_before",false},
        {"general v1 day",Template(body:GeneralNoBaja+" BAJA",buttons:["Cancelar cita"]),KapsoClient.GeneralDayWindow,true},
        {"general v2 day",Template(body:GeneralNoBaja,buttons:["Cancelar cita","No más recordatorios"]),KapsoClient.GeneralDayWindow,true},
        {"general v2 day, without BAJA nor stop",Template(body:GeneralNoBaja,buttons:["Cancelar cita"]),KapsoClient.GeneralDayWindow,false},
        {"general v2 day, with Confirmar (A13)",Template(body:GeneralNoBaja,buttons:["Confirmar","Cancelar cita","No más recordatorios"]),KapsoClient.GeneralDayWindow,false},
        {"general v1 hour",Template(body:GeneralNoBaja,footer:"Escriba BAJA."),KapsoClient.GeneralHourWindow,true},
        {"general v2 hour",Template(body:GeneralNoBaja,buttons:["No más recordatorios"]),KapsoClient.GeneralHourWindow,true},
        {"general hour, without BAJA nor stop",Template(body:GeneralNoBaja),KapsoClient.GeneralHourWindow,false},
        {"general hour, cancel button",Template(body:GeneralNoBaja,buttons:["Cancelar cita","No más recordatorios"]),KapsoClient.GeneralHourWindow,false},
    };
    // Criterio 2: the shape that may be sent depends on the window.
    [Theory][MemberData(nameof(Shapes))]
    public void TemplateShapeMatchesItsWindow(string _,JsonElement template,string window,bool expected)=>Assert.Equal(expected,KapsoClient.ValidReminderTemplate(template,window));
}

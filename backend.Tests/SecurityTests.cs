using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Recepcion;
using Recepcion.Integrations;
using Xunit;
public sealed class SecurityTests {
    [Theory][InlineData("")][InlineData("garbage")][InlineData("00")][InlineData("sha256=abc")]
    public void InvalidSignaturesFailClosed(string signature)=>Assert.False(Rules.VerifySignature(Encoding.UTF8.GetBytes("{}"),signature,"secret"));
    [Fact]public void SignatureCoversExactBody(){var body=Encoding.UTF8.GetBytes("{\"text\":\"á\"}");var sig=Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"),body));Assert.True(Rules.VerifySignature(body,sig,"secret"));Assert.False(Rules.VerifySignature(Encoding.UTF8.GetBytes("{}"),sig,"secret"));Assert.False(Rules.VerifySignature(body,sig,"different"));}
    [Fact]public void EmptySecretNeverAccepted()=>Assert.False(Rules.VerifySignature([],Convert.ToHexString(HMACSHA256.HashData([],[])),""));
    [Theory][InlineData("50370000000","50370000000")][InlineData("+503 7000-0000","50370000000")]
    public void PhoneCanonicalization(string input,string output)=>Assert.Equal(output,Rules.Phone(input));
    [Theory][InlineData("123")][InlineData("not a number")][InlineData("1234567890123456")]
    public void BadPhoneRejected(string phone)=>Assert.Throws<ArgumentException>(()=>Rules.Phone(phone));
    [Fact]public void HashUsesSecretAndNormalizedPhone(){Assert.Equal(Rules.PhoneHash("+503 7000-0000","secret"),Rules.PhoneHash("50370000000","secret"));Assert.NotEqual(Rules.PhoneHash("50370000000","secret"),Rules.PhoneHash("50370000000","other"));}
    [Fact]public void WhatsAppWindowBoundary(){var now=DateTimeOffset.UtcNow;Assert.False(Rules.WithinWindow(null,now));Assert.False(Rules.WithinWindow(now.AddHours(-24),now));Assert.False(Rules.WithinWindow(now.AddMinutes(1),now));Assert.True(Rules.WithinWindow(now.AddHours(-23),now));}
    [Theory][InlineData("[]")][InlineData("{\"roles\":42}")][InlineData("invalid")]
    public void MalformedRolesDenied(string json){var p=new ClaimsPrincipal(new ClaimsIdentity([new Claim("realm_access",json)],"test"));Assert.Null(Identity.MapRole(p));}
    // Hospital's seven PRD §3 roles are the whole vocabulary: nothing Recepción once invented, no
    // service capability and no self-asserted `role` claim opens a workspace.
    [Theory][InlineData("Administrador","admin")][InlineData("Médicos","doctor")][InlineData("Odontólogos","doctor")][InlineData("Nutricionistas","doctor")][InlineData("Recepción","agent")][InlineData("Admisión","agent")]
    [InlineData("Enfermería",null)][InlineData("reception-agent",null)][InlineData("platform_admin",null)][InlineData("supervisor",null)][InlineData("Supervisor",null)][InlineData("admin",null)][InlineData("agent",null)][InlineData("doctor",null)]
    public void OnlyHospitalRolesMap(string hospitalRole,string? expected){var p=new ClaimsPrincipal(new ClaimsIdentity([new Claim("realm_access","{\"roles\":[\""+hospitalRole+"\"]}"),new Claim("role","admin")],"test"));Assert.Equal(expected,Identity.MapRole(p));}
    [Theory][InlineData("Recepción","reception-agent",null,null)][InlineData("Administrador","reception-agent",null,null)][InlineData("Recepción",null,"service-account-x",null)][InlineData("Recepción",null,"ana","agent")]
    public void ServiceAccountsNeverMapToARole(string role,string? extraRole,string? username,string? expected)
    {
        var roles=string.Join(",",new[]{role,extraRole}.Where(x=>x is not null).Select(x=>"\""+x+"\""));
        var claims=new List<Claim>{new("realm_access","{\"roles\":["+roles+"]}")}; if(username is not null)claims.Add(new("preferred_username",username));
        Assert.Equal(expected,Identity.MapRole(new ClaimsPrincipal(new ClaimsIdentity(claims,"test"))));
    }
    [Fact]public void ClinicalRoleMapsButDoesNotBecomeAdmin(){var p=new ClaimsPrincipal(new ClaimsIdentity([new Claim("realm_access","{\"roles\":[\"Médicos\"]}")],"test"));Assert.Equal("doctor",Identity.MapRole(p));Assert.False(new CurrentUser{Role="doctor"}.Admin);}
    [Fact]public void CalendarIdsAreStableAndTenantBound(){var a=Guid.NewGuid();var b=Guid.NewGuid();var appointment=Guid.NewGuid();Assert.Equal(GoogleCalendarClient.EventId(a,appointment),GoogleCalendarClient.EventId(a,appointment));Assert.NotEqual(GoogleCalendarClient.EventId(a,appointment),GoogleCalendarClient.EventId(b,appointment));Assert.Matches("^[0-9a-f]{64}$",GoogleCalendarClient.EventId(a,appointment));}
}

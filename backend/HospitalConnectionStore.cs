using System.Text.Json;
using Microsoft.EntityFrameworkCore;
namespace Recepcion;

// Request/worker-scope cache only; credentials remain encrypted in PostgreSQL and /keys persists.
public sealed class HospitalConnectionStore(CrmDb db, IConfiguration config)
{
    readonly Dictionary<Guid,IConfigurationSection> cache = new();
    public IConfigurationSection Section(Guid tenant)
    {
        if (cache.TryGetValue(tenant, out var section)) return section;
        var values = Defaults(tenant, config);
        foreach (var pair in config.GetSection($"Hospital:Tenants:{tenant:D}").GetChildren()) values[pair.Key]=pair.Value;
        var saved = db.Tenants.AsNoTracking().Where(x=>x.Id==tenant).Select(x=>x.HospitalConnection).SingleOrDefault();
        if (saved is not null)
            foreach (var pair in JsonSerializer.Deserialize<Dictionary<string,string?>>(saved)!) values[pair.Key]=pair.Value;
        section = new ConfigurationBuilder().AddInMemoryCollection(values.Select(x=>new KeyValuePair<string,string?>("connection:"+x.Key,x.Value))).Build().GetSection("connection");
        cache[tenant]=section;
        return section;
    }
    // What makes a hospital connect by signing in: one installation-wide Hospital API and the
    // service account Hospital's realm job declares for every tenant, `recepcion-service-<tenant>`.
    // Nothing here is per hospital, so nobody types a UUID, a URL or a secret to connect one.
    // Prescription delivery is NOT defaulted: it stays an explicit per-hospital authorization.
    public static Dictionary<string,string?> Defaults(Guid tenant, IConfiguration config)
    {
        var issuer = config["KEYCLOAK_INTERNAL_ISSUER"] is { Length: > 0 } internalIssuer ? internalIssuer : config["Auth:Authority"];
        if (string.IsNullOrWhiteSpace(config["HOSPITAL_API_URL"]) || string.IsNullOrWhiteSpace(config["HOSPITAL_SERVICE_CLIENT_SECRET"]) || string.IsNullOrWhiteSpace(issuer)) return new();
        return new()
        {
            ["BaseUrl"]=config["HOSPITAL_API_URL"], ["PublicUrl"]=config["HOSPITAL_PUBLIC_URL"],
            ["TokenEndpoint"]=issuer.TrimEnd('/')+"/protocol/openid-connect/token",
            ["ClientId"]=$"recepcion-service-{tenant:D}", ["ClientSecret"]=config["HOSPITAL_SERVICE_CLIENT_SECRET"], ["UsePatientAgenda"]="true"
        };
    }
}
public record HospitalConnectInput(string BaseUrl, string PublicUrl, string ClientId, string ClientSecret);
public static class HospitalConnectionRules
{
    public static Dictionary<string,string?> Validate(HospitalConnectInput input, IConfiguration config)
    {
        static Uri Url(string value)
        {
            if (!Uri.TryCreate(value?.Trim(),UriKind.Absolute,out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath!="/")
                throw new ArgumentException("Usa la dirección base del hospital, sin rutas, credenciales ni parámetros.");
            return uri;
        }
        var api=Url(input.BaseUrl); var web=Url(input.PublicUrl);
        var allowed=(config["HOSPITAL_ALLOWED_API_ORIGINS"]??"").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(!allowed.Any(x=>Uri.TryCreate(x,UriKind.Absolute,out var uri)&&uri.GetLeftPart(UriPartial.Authority)==api.GetLeftPart(UriPartial.Authority)))
            throw new ArgumentException("Esta API no está habilitada para la conexión. Añádela a HOSPITAL_ALLOWED_API_ORIGINS en Easypanel.");
        if(config["HOSPITAL_PUBLIC_URL"] is {Length:>0} fixedPublic && (!Uri.TryCreate(fixedPublic,UriKind.Absolute,out var fixedUri) || fixedUri.GetLeftPart(UriPartial.Authority)!=web.GetLeftPart(UriPartial.Authority)))
            throw new ArgumentException("La dirección pública de Hospital debe coincidir con la configurada para esta instalación (HOSPITAL_PUBLIC_URL).");
        var development=config["ASPNETCORE_ENVIRONMENT"]=="Development";
        if(!development && web.Scheme!="https") throw new ArgumentException("La dirección pública de Hospital debe usar HTTPS.");
        var issuer=config["KEYCLOAK_INTERNAL_ISSUER"]??config["Auth:Authority"];
        if(!Uri.TryCreate(issuer,UriKind.Absolute,out var identity)||identity.Scheme is not("http" or "https")||!string.IsNullOrEmpty(identity.UserInfo)||!string.IsNullOrEmpty(identity.Query)||!string.IsNullOrEmpty(identity.Fragment))
            throw new ArgumentException("Configura primero la identidad compartida de Hospital en Easypanel.");
        return new(){["BaseUrl"]=api.AbsoluteUri,["PublicUrl"]=web.GetLeftPart(UriPartial.Authority),["ClientId"]=Rules.Required(input.ClientId,150),["ClientSecret"]=Rules.Required(input.ClientSecret,4096),["TokenEndpoint"]=issuer!.TrimEnd('/')+"/protocol/openid-connect/token",["AccessToken"]=null,["UsePatientAgenda"]="true"};
    }
}

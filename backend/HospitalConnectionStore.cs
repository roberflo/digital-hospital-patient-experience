using System.Security.Cryptography;
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
    // MR-38: this hospital's own credential for `recepcion-service-<tenant>` (Keycloak client-jwt). The private
    // half lives only in the encrypted HospitalConnection; Keycloak reads the public half from /hospital-jwks/<tenant>.
    // Created on first need, before the first token request makes Keycloak fetch it. Null for a hospital with no row.
    // Creating and rotating take the tenant's advisory lock (the one Save takes) and re-read under it, so concurrent
    // first uses agree on one key. A rotation keeps nothing of the old key: Keycloak caches every key a JWKS read returns and
    // re-reads only on an unknown kid, so an old key published next to the new one would keep verifying after the rotation.
    public async Task<ECDsa?> SigningKeyAsync(Guid tenant, bool rotate = false, CancellationToken ct = default)
    {
        if (!rotate && Section(tenant)["SigningKey"] is { Length: > 0 } cached) return Import(cached);
        using var lease = await ConversationService.Lock(db, tenant, ct);
        var row = await db.Tenants.AsNoTracking().Where(x => x.Id == tenant).Select(x => new { x.HospitalConnection }).SingleOrDefaultAsync(ct);
        if (row is null) return null;
        var values = row.HospitalConnection is null ? new() : JsonSerializer.Deserialize<Dictionary<string,string?>>(row.HospitalConnection)!;
        var stored = values.GetValueOrDefault("SigningKey");
        if (!rotate && stored is { Length: > 0 }) { cache.Remove(tenant); return Import(stored); }
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        foreach (var legacy in new[] { "PreviousSigningKey", "PreviousPublicKey", "RotatedAt" }) values.Remove(legacy); // earlier versions of this branch
        values["SigningKey"] = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
        var json = JsonSerializer.Serialize(values);
        // Outside the request's change tracker: a token request never flushes what the handler has pending.
        await db.Tenants.Where(x => x.Id == tenant).ExecuteUpdateAsync(x => x.SetProperty(t => t.HospitalConnection, json), ct);
        cache.Remove(tenant);
        return key;
    }
    /// <summary>The public JWK of a signing key; the kid is its RFC 7638 thumbprint.</summary>
    public static Dictionary<string,string> Jwk(ECDsa key)
    {
        var q = key.ExportParameters(false).Q;
        static string B64(byte[] v) => System.Buffers.Text.Base64Url.EncodeToString(v);
        var (x, y) = (B64(q.X!), B64(q.Y!));
        var kid = B64(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{x}\",\"y\":\"{y}\"}}")));
        return new() { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = x, ["y"] = y, ["kid"] = kid, ["use"] = "sig", ["alg"] = "ES256" };
    }
    static ECDsa Import(string pkcs8) { var key = ECDsa.Create(); key.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8), out _); return key; }
    /// <summary>What /hospital-jwks/<tenant> publishes: the current public key only, or nothing. Never creates one.</summary>
    public Dictionary<string,string>[] PublicKeys(Guid tenant)
    {
        if (Section(tenant)["SigningKey"] is not { Length: > 0 } current) return [];
        using var key = Import(current); return [Jwk(key)];
    }
    // What makes a hospital connect by signing in: one installation-wide Hospital API and the
    // service account Hospital's realm job declares for every tenant, `recepcion-service-<tenant>`, which
    // authenticates with this hospital's own key (SigningKeyAsync). Nobody types a UUID, a URL or a secret.
    // Prescription delivery is NOT defaulted: it stays an explicit per-hospital authorization.
    public static Dictionary<string,string?> Defaults(Guid tenant, IConfiguration config)
    {
        var issuer = config["KEYCLOAK_INTERNAL_ISSUER"] is { Length: > 0 } internalIssuer ? internalIssuer : config["Auth:Authority"];
        if (string.IsNullOrWhiteSpace(config["HOSPITAL_API_URL"]) || string.IsNullOrWhiteSpace(issuer)) return new();
        var values = new Dictionary<string,string?>
        {
            ["BaseUrl"]=config["HOSPITAL_API_URL"], ["PublicUrl"]=config["HOSPITAL_PUBLIC_URL"],
            ["TokenEndpoint"]=issuer.TrimEnd('/')+"/protocol/openid-connect/token",
            ["ClientId"]=$"recepcion-service-{tenant:D}"
        };
        // ponytail: MR-38 transition only — the shared secret is the fallback HospitalClient tries after invalid_client.
        // Retired in deploy step 3 (docs/hospital-integration.md «MR-38»): remove HOSPITAL_SERVICE_CLIENT_SECRET, then this line.
        // The fallback itself stays: it serves explicit per-hospital secrets (Hospital__Tenants__<id>__ClientSecret, PUT connection).
        if (config["HOSPITAL_SERVICE_CLIENT_SECRET"] is { Length: > 0 } shared) values["ClientSecret"] = shared;
        return values;
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
        return new(){["BaseUrl"]=api.AbsoluteUri,["PublicUrl"]=web.GetLeftPart(UriPartial.Authority),["ClientId"]=Rules.Required(input.ClientId,150),["ClientSecret"]=Rules.Required(input.ClientSecret,4096),["TokenEndpoint"]=issuer!.TrimEnd('/')+"/protocol/openid-connect/token",["AccessToken"]=null};
    }
}

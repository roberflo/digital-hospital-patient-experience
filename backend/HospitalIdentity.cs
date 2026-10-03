using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Recepcion;

public static class HospitalIdentity
{
    public static void Configure(JwtBearerOptions options, IConfiguration config, bool development)
    {
        var authority = config["Auth:Authority"]!;
        options.MapInboundClaims = false;
        options.Authority = authority;
        options.Audience = config["Auth:Audience"] ?? "hospital-api";
        options.RequireHttpsMetadata = !development;
        options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = authority, ValidateAudience = true, ValidAudience = options.Audience, ValidateLifetime = true, ValidateIssuerSigningKey = true };
        if (development && config["KEYCLOAK_INTERNAL_ISSUER"] is { Length: > 0 } internalIssuer)
            options.BackchannelHttpHandler = new DiscoveryHandler(new Uri(authority.TrimEnd('/') + "/"), new Uri(internalIssuer.TrimEnd('/') + "/"));
    }

    // Only the configured IdP's backchannel is routed onto the local Docker network.
    // Token issuer/audience/signature verification remains mandatory.
    sealed class DiscoveryHandler(Uri external, Uri internalUri) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is { } uri && external.IsBaseOf(uri))
                request.RequestUri = new Uri(internalUri, external.MakeRelativeUri(uri));
            return base.SendAsync(request, ct);
        }
    }
}

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

namespace Streaik.Gateway.AzureAuth;

internal static class EntraGatewayAuth
{
    public const string Access = "Streaik.Access";

    public static void Add(
        IServiceCollection services,
        IConfigurationSection entra)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(entra);

        services.AddAuthorizationBuilder()
            .AddPolicy(Access, policy =>
                policy.RequireAuthenticatedUser());
    }
}

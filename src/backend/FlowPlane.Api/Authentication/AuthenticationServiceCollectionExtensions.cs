using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace FlowPlane.Api.Authentication;

internal static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddFlowPlaneAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration
            .GetRequiredSection(FlowPlaneAuthenticationOptions.SectionName)
            .Get<FlowPlaneAuthenticationOptions>()
            ?? throw new InvalidOperationException(
                $"{FlowPlaneAuthenticationOptions.SectionName} must be configured.");
        options.Validate(environment);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwtBearerOptions =>
            {
                jwtBearerOptions.MetadataAddress = options.MetadataAddress;
                jwtBearerOptions.RequireHttpsMetadata = options.RequireHttpsMetadata;
                jwtBearerOptions.MapInboundClaims = false;
                jwtBearerOptions.IncludeErrorDetails = false;
                jwtBearerOptions.TokenValidationParameters = new TokenValidationParameters
                {
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    NameClaimType = "name"
                };
            });
        services.AddAuthorization();

        return services;
    }
}

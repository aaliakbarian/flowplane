using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

using FlowPlane.Api.Agents;
using FlowPlane.Api.Authentication;
using FlowPlane.Api.Providers;
using FlowPlane.Infrastructure;
using FlowPlane.Providers.Ssis.Configuration;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

using Npgsql;

namespace FlowPlane.Agent.IntegrationHost;

// Test-only child process: production routes, service, certificate handler and PostgreSQL authority.
// All secret-bearing bootstrap material arrives through the parent's private anonymous stdin pipe, never argv/files.
internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var token = deadline.Token;
            var line = await Console.In.ReadLineAsync(token);
            if (line is null || line.Length > 65536) return 2;
            var input = JsonSerializer.Deserialize<Input>(line) ?? throw new InvalidOperationException();
            var connection = new NpgsqlConnectionStringBuilder(input.ConnectionString);
            // Never open an unverified target, including the development database. Check the exact immutable identity before hosting.
            if (!Regex.IsMatch(input.RunId, "^[a-f0-9]{32}$") ||
                !Regex.IsMatch(input.Name, "^flowplane_agent_" + input.RunId[..12] + "_[a-f0-9]{32}$") ||
                connection.Database != input.Name || connection.Host != "127.0.0.1" || connection.Port != 5432 ||
                input.Oid <= 0 || input.Marker is null || !input.Marker.StartsWith("flowplane-test:" + input.RunId + ":", StringComparison.Ordinal))
                return 2;
            await using (var db = new NpgsqlConnection(connection.ConnectionString))
            {
                await db.OpenAsync(token);
                await using var command = new NpgsqlCommand("SELECT oid::bigint, pg_get_userbyid(datdba), shobj_description(oid,'pg_database') FROM pg_database WHERE datname=current_database()", db);
                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token) || reader.GetInt64(0) != input.Oid || reader.GetString(1) != input.Owner ||
                    reader.GetString(2) != input.Marker || input.Owner != connection.Username) return 2;
            }
            var bytes = Convert.FromBase64String(input.ServerPfx);
            using var server = X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.EphemeralKeySet);
            CryptographicOperations.ZeroMemory(bytes);
            AgentControlHosting.ValidateServerCertificate(server);
            using var publicKey = RSA.Create();
            publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(input.SigningPublicKey), out _);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", Args = [] });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FlowPlane"] = connection.ConnectionString,
                ["FlowPlane:Authentication:Issuer"] = input.Issuer,
                ["FlowPlane:Authentication:Audience"] = "flowplane-api",
                ["FlowPlane:Authentication:MetadataAddress"] = input.Issuer + "/.well-known/openid-configuration",
                ["FlowPlane:Operators:Allowed:0:Issuer"] = input.Issuer,
                ["FlowPlane:Operators:Allowed:0:Subject"] = "operator",
                ["FlowPlane:Operators:Allowed:1:Issuer"] = input.Issuer,
                ["FlowPlane:Operators:Allowed:1:Subject"] = "other-operator",
                ["FlowPlane:Ssis:AllowedEndpoints:0:Host"] = "sql.example.test",
                ["FlowPlane:Ssis:AllowedEndpoints:0:Port"] = "1433",
            });
            builder.Logging.ClearProviders();
            builder.Services.AddFlowPlanePostgreSql(builder.Configuration);
            builder.Services.AddFlowPlaneAuthentication(builder.Configuration, builder.Environment);
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var metadata = new OpenIdConnectConfiguration { Issuer = input.Issuer };
                metadata.SigningKeys.Add(new RsaSecurityKey(publicKey) { KeyId = "integration" });
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            });
            builder.Services.AddProviderApi(builder.Configuration);
            builder.Services.AddSingleton<SsisProviderConfigurationCodec>();
            builder.AddAgentControl(); // Register the real control plane without its production wildcard bindings or certificate files.
            builder.Services.AddSingleton(new AgentControlOptions { Enabled = true });
            builder.Services.AddHostedService<AgentPinLoader>();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(server));
                options.Listen(IPAddress.Loopback, 0, listen => AgentControlHosting.ConfigureAgentListener(listen, server,
                    options.ApplicationServices.GetRequiredService<AgentPinRegistry>(),
                    options.ApplicationServices.GetRequiredService<AgentListenerBoundary>()));
            });
            await using var app = builder.Build();
            app.UseAgentControlBoundary();
            app.UseAuthentication();
            app.UseAuthorization();
            app.Use(async (context, next) =>
            {
                context.Response.Headers["X-Test-Connection"] = context.Connection.Id;
                context.Response.Headers["X-Test-Process"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await next(context);
            });
            app.MapAgentControl();
            await app.StartAsync(token);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId,
                addresses,
                configuration = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
                    .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration
            }));
            await Console.In.ReadLineAsync(token);
            await app.StopAsync(token);
            return 0;
        }
        catch
        {
            // Never emit bootstrap input, certificate, connection strings, exceptions or request bodies.
            Console.Error.WriteLine("Isolated agent integration host failed safely.");
            return 1;
        }
    }

    private sealed record Input(string ConnectionString, string RunId, string Name, long Oid, string Owner, string Marker,
        string ServerPfx, string SigningPublicKey, string Issuer);
}

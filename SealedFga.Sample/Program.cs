using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenFga.Sdk.Client;
using SealedFga;
using SealedFga.Sample.Auth;
using SealedFga.Sample.Database;
using SealedFga.Sample.Secret;

namespace SealedFga.Sample;

// Declared as a (non-static) partial class rather than `static` so it can be used as the
// TEntryPoint for WebApplicationFactory<Program> in the integration tests.
public partial class Program {
    public static async Task Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container
        builder.Services.AddDbContext<SealedFgaSampleContext>((sp, options) => {
                options.UseInMemoryDatabase("SealedFgaSampleDb");
                options.AddSealedFga(sp);
            }
        );

        // Add authentication
        builder.Services.AddAuthentication("MockScheme")
               .AddScheme<AuthenticationSchemeOptions, MockAuthenticationHandler>("MockScheme", _ => { });
        builder.Services.AddAuthorization();

        // The sample's OpenFGA store is provisioned dynamically by docker-compose (a fresh store id on
        // every run), so it discovers the store + model at startup and registers its own OpenFgaClient.
        // Because SealedFGA registers its client with TryAddSingleton, this explicit registration (added
        // first) wins. A normal consumer with a stable store just sets ApiUrl / StoreId /
        // AuthorizationModelId in the "SealedFga" configuration section and omits this block entirely.
        var apiUrl = builder.Configuration["SealedFga:ApiUrl"] ?? "http://localhost:8080";
        builder.Services.AddSingleton<OpenFgaClient>(_ => {
                using var storeLookup = new OpenFgaClient(new ClientConfiguration { ApiUrl = apiUrl });
                var storeId = storeLookup.ListStores(null).Result.Stores[0].Id;
                using var modelLookup =
                    new OpenFgaClient(new ClientConfiguration { ApiUrl = apiUrl, StoreId = storeId });
                var authModelId = modelLookup.ReadAuthorizationModels().Result.AuthorizationModels[0].Id;
                return new OpenFgaClient(new ClientConfiguration {
                        ApiUrl = apiUrl,
                        StoreId = storeId,
                        AuthorizationModelId = authModelId,
                    }
                );
            }
        );

        // Configure SealedFGA (binds the "SealedFga" config section). The background outbox drainer runs
        // by default (SealedFgaOptions.RunOutboxDrainer = true).
        builder.Services.ConfigureSealedFga<SealedFgaSampleContext>(builder.Configuration);

        var app = builder.Build();

        // TODO: Maybe move SealedFGA related config into some init method
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRouting();
        app.MapControllers();
        app.UseSealedFga();

        // Seed the database
        using (var scope = app.Services.CreateScope()) {
            var context = scope.ServiceProvider.GetRequiredService<SealedFgaSampleContext>();
            await context.Database.EnsureCreatedAsync();
            await context.AddDummyData();
        }

        await app.RunAsync();
    }
}

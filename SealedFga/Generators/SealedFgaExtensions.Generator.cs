using System.Collections.Generic;
using SealedFga.Models;

namespace SealedFga.Generators;

public static class SealedFgaExtensionsGenerator {
    public static GeneratedFile Generate()
        => new(
            "SealedFgaExtensions.g.cs",
            """

            /// <summary>
            ///     Contains all extension methods the SealedFga library provides/uses.
            /// </summary>
            public static class SealedFgaExtensions {
                /// <summary>
                ///    Retrieves all SealedFGA ID types from the assembly.
                /// </summary>
                private static IEnumerable<Type> GetSealedFgaIdTypes() {
                    var assembly = Assembly.GetExecutingAssembly();
                    var idTypes = assembly.GetTypes()
                                          .Where(t => t.GetCustomAttribute<SealedFgaTypeIdAttribute>() is not null);

                    return idTypes;
                }

                /// <summary>
                ///     Configures the EF Core model builder to use the SealedFGA ID types.
                ///     Has to be called from the DbContext's ConfigureConventions method.
                /// </summary>
                public static void ConfigureSealedFga(this ModelConfigurationBuilder configurationBuilder) {
                    // Retrieve all SealedFGA ID Types from the assembly
                    var idTypes = GetSealedFgaIdTypes();

                    // Retrieve contained EF Core ValueConverter classes and register them
                    foreach (var type in idTypes) {
                        var valueConverters = type.GetNestedTypes().Where(t => t.IsSubclassOf(typeof(ValueConverter)));
                        foreach (var valueConverter in valueConverters) {
                            configurationBuilder
                               .Properties(type)
                               .HaveConversion(valueConverter);
                        }
                    }
                }

                /// <summary>
                ///     Configures SealedFGA services with the specified options.
                /// </summary>
                /// <param name="services">The service collection to configure.</param>
                /// <param name="configure">Optional configuration action for SealedFgaOptions.</param>
                /// <returns>The service collection for method chaining.</returns>
                public static IServiceCollection ConfigureSealedFga<TDbContext>(
                    this IServiceCollection services,
                    Action<SealedFgaOptions>? configure = null
                ) where TDbContext : DbContext
                {
                    services.AddScoped<SealedFgaService>();
                    services.AddScoped<SealedFgaSaveChangesInterceptor>();
                    services.AddHostedService<SealedFgaOutboxHostedService<TDbContext>>();
                    services.AddControllers(options => {
                            options.ModelBinderProviders.Insert(0, new SealedFgaModelBinderProvider<TDbContext>());
                        }
                    );

                    // Always register the options so IOptions<SealedFgaOptions> resolves (to defaults
                    // when no configure action is supplied); SealedFgaService depends on it.
                    services.AddOptions<SealedFgaOptions>();
                    if (configure != null) {
                        services.Configure(configure);
                    }
                    return services;
                }

                /// <summary>
                ///     Adds all SealedFga DB related options.
                /// </summary>
                public static DbContextOptionsBuilder AddSealedFga(
                    this DbContextOptionsBuilder options,
                    IServiceProvider serviceProvider
                ) {
                    var interceptor = serviceProvider.GetRequiredService<SealedFgaSaveChangesInterceptor>();
                    options.AddInterceptors(interceptor);

                    // Auto-register the SealedFGA outbox entity into the model without the consumer
                    // having to touch their OnModelCreating.
                    options.ReplaceService<IModelCustomizer, SealedFgaModelCustomizer>();

                    return options;
                }

                /// <summary>
                ///     Adds the SealedFga middleware to the web application.
                /// </summary>
                public static IApplicationBuilder UseSealedFga(this IApplicationBuilder app) {
                    app.UseMiddleware<SealedFgaExceptionHandlerMiddleware>();

                    return app;
                }
            }
            """,
            new HashSet<string>([
                    "Microsoft.AspNetCore.Builder",
                    "Microsoft.EntityFrameworkCore",
                    "Microsoft.EntityFrameworkCore.Infrastructure",
                    "Microsoft.EntityFrameworkCore.Storage.ValueConversion",
                    "Microsoft.Extensions.DependencyInjection",
                    "Microsoft.Extensions.Options",
                    Settings.AttributesNamespace,
                    Settings.ModelBinderNamespace,
                    Settings.FgaNamespace,
                    Settings.FgaNamespace + ".Outbox",
                    Settings.MiddlewareNamespace,
                    "System",
                    "System.Collections.Generic",
                    "System.Linq",
                    "System.Reflection",
                ]
            )
        );
}

using Autofac;
using Microsoft.Extensions.Configuration;
using Refund.Configuration.Constants;

namespace Refund.Configuration;

/// <summary>
/// Provides extension methods to register configuration classes with the Autofac dependency injection container.
/// These methods facilitate a standardized approach to configuration management across the application.
/// </summary>
public static class ConfigurationExtension
{
    /// <summary>
    /// Registers all application configuration classes with the Autofac container
    /// </summary>
    /// <param name="containerBuilder">The Autofac container builder</param>
    /// <returns>The same container builder instance for method chaining</returns>
    /// <remarks>
    /// This method registers various configuration classes as singletons in the DI container:
    /// - AuthServiceConfiguration - for OAuth/OpenID Connect authentication
    /// - AuthenticationConfiguration - for general authentication settings
    /// 
    /// Each configuration is loaded from the appropriate section of the application configuration
    /// using the section keys defined in CommonConstants.
    /// 
    /// This method is called during application startup in Program.cs through the Host.ConfigureContainer 
    /// method with Autofac's ContainerBuilder:
    /// 
    /// builder.Host.ConfigureContainer&lt;ContainerBuilder&gt;(containerBuilder => 
    ///     containerBuilder.RegisterConfigurations());
    /// 
    /// After this registration, these configuration objects become available for injection
    /// throughout the application via constructor injection.
    /// </remarks>
    public static ContainerBuilder RegisterConfigurations(this ContainerBuilder containerBuilder)
    {
        // Register OAuth service configuration
        containerBuilder.Register(context => context.Resolve<IConfiguration>()
                                                    .GetSection(CommonConstants.AuthService)
                                                    .LoadAuthServiceConfiguration())
                        .SingleInstance();

        // Register general authentication configuration from appsettings
        containerBuilder.Register(context => context.Resolve<IConfiguration>()
                                                    .GetSection(CommonConstants.Authentication)
                                                    .LoadAuthenticationConfiguration())
                        .SingleInstance();

        return containerBuilder;
    }
}
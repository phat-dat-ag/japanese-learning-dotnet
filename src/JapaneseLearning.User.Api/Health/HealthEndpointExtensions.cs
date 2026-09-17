using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace JapaneseLearning.User.Api.Health;

public static class HealthEndpointExtensions
{
    public static void MapServiceHealthChecks(this WebApplication app)
    {
        // Serving HTTP proves liveness; dependencies belong only to readiness.
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false
        }).AllowAnonymous();

        var readiness = new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready")
        };
        app.MapHealthChecks("/health/ready", readiness).AllowAnonymous();
        app.MapHealthChecks("/health", readiness).AllowAnonymous();
    }
}

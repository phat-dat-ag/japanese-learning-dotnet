using JapaneseLearning.User.Api.Authentication;
using JapaneseLearning.User.Api.Common.Errors;
using JapaneseLearning.User.Api.Common.Logging;
using JapaneseLearning.User.Api.Health;
using JapaneseLearning.User.Api.Observability;
using JapaneseLearning.User.Application;
using JapaneseLearning.User.Infrastructure;
using Serilog;
using System.Text.Json.Serialization;

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("ServiceName", "JapaneseLearning.User.Api")
    .Enrich.WithProperty("Environment", Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Production)
    .WriteTo.Console(new SafeJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders();
    builder.Services.AddSerilog((services, logging) =>
        logging.ConfigureService(builder.Configuration, builder.Environment));

    builder.Services
        .AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.DefaultIgnoreCondition =
                JsonIgnoreCondition.WhenWritingNull;
        });

    builder.Services.AddApiErrorHandling();
    builder.Services.AddSingleton<ApplicationMetrics>();

    builder.Services.AddApplication();

    builder.Services.AddInfrastructure(
        builder.Configuration);

    builder.Services.AddJwtAuthentication();

    builder.Services.AddAuthorization();

    builder.Services.AddHttpContextAccessor();

    builder.Services.AddSwaggerGen(options =>
    {
        options.AddSecurityDefinition(
            "Bearer",
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = Microsoft.OpenApi.Models.ParameterLocation.Header,
                Description = "Enter JWT token"
            });

        options.AddSecurityRequirement(
            new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
            {
                {
                    new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                    {
                        Reference =
                            new Microsoft.OpenApi.Models.OpenApiReference
                            {
                                Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                                Id = "Bearer"
                            }
                    },
                    Array.Empty<string>()
                }
            });
    });

    var app = builder.Build();

    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseRouting();
    app.UseApplicationMetrics();
    app.UseApiErrorHandling();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    if (app.Configuration.GetValue<bool>("Http:RedirectToHttps", true))
    {
        app.UseHttpsRedirection();
    }

    app.UseAuthentication();

    app.UseAuthorization();

    app.MapControllers();

    app.MapServiceHealthChecks();
    app.MapApplicationMetrics();

    app.Run();
}
catch (Exception exception)
{
    Log.Fatal(exception, "User service terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
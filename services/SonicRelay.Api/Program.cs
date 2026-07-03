using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using SonicRelay.Api.Endpoints;
using SonicRelay.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSonicRelayInfrastructure(builder.Configuration);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Postgres") ?? string.Empty, name: "postgres")
    .AddRedis(builder.Configuration["Redis:ConnectionString"] ?? string.Empty, name: "redis");
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AuthenticatedUser", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("CanRegisterDevice", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("CanCreateSession", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("CanJoinSession", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("CanPublishSession", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("CanViewSession", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));
});

var app = builder.Build();

if (app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapAuthEndpoints();
app.MapDeviceEndpoints();
app.MapSessionEndpoints();
app.MapSignalingWebSocketEndpoint();

app.Run();

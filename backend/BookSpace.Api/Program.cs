using System.Diagnostics.CodeAnalysis;
using System.Text;
using BookSpace.Api.Security;
using BookSpace.Application;
using BookSpace.Application.Security;
using BookSpace.Infrastructure;
using BookSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserContext, CurrentUserContext>();

var authConfig = builder.Configuration.GetSection("Auth");
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Otherwise short claim names like "sub" get silently remapped to legacy XML-namespace
        // claim URIs, and every claim lookup elsewhere has to know about that translation.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authConfig["Issuer"],
            ValidateAudience = true,
            ValidAudience = authConfig["Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authConfig["SigningKey"]
                ?? throw new InvalidOperationException("Auth:SigningKey is not configured."))),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

// Secure by default: any endpoint added later requires an authenticated caller unless it explicitly
// opts out with [AllowAnonymous] - the same "can't be forgotten" principle as the tenant filter below.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    await app.Services.MigrateAndSeedDevelopmentDatabaseAsync();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/health/db", async (BookSpaceDbContext dbContext, CancellationToken cancellationToken) =>
{
    var isConnected = await dbContext.Database.CanConnectAsync(cancellationToken);
    return Results.Ok(new { database = dbContext.Database.GetDbConnection().Database, status = isConnected ? "connected" : "unreachable" });
}).AllowAnonymous();

app.Run();

[ExcludeFromCodeCoverage(Justification = "Composition root - wiring is covered by integration tests running against it, not unit-tested directly.")]
public partial class Program;

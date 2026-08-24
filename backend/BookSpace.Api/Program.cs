using System.Diagnostics.CodeAnalysis;
using BookSpace.Infrastructure;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    await app.Services.MigrateAndSeedDevelopmentDatabaseAsync();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.MapGet("/health/db", async (BookSpaceDbContext dbContext, CancellationToken cancellationToken) =>
{
    var isConnected = await dbContext.Database.CanConnectAsync(cancellationToken);
    return Results.Ok(new { database = dbContext.Database.GetDbConnection().Database, status = isConnected ? "connected" : "unreachable" });
});

app.Run();

[ExcludeFromCodeCoverage(Justification = "Composition root - wiring is covered by integration tests running against it, not unit-tested directly.")]
public partial class Program;

using System.Diagnostics.CodeAnalysis;
using BookSpace.Api.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("BookSpace")
    ?? throw new InvalidOperationException("Connection string 'BookSpace' is not configured.");
builder.Services.AddDbContext<BookSpaceDbContext>(options => options.UseSqlServer(connectionString));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    using var seedScope = app.Services.CreateScope();
    var dbContext = seedScope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
    await dbContext.Database.MigrateAsync();
    await DevelopmentSeeder.SeedAsync(dbContext);
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.MapGet("/health/db", async (IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var connectionString = configuration.GetConnectionString("BookSpace")
        ?? throw new InvalidOperationException("Connection string 'BookSpace' is not configured.");

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    return Results.Ok(new { database = connection.Database, status = "connected" });
});

app.Run();

[ExcludeFromCodeCoverage(Justification = "Composition root - wiring is covered by integration tests running against it, not unit-tested directly.")]
public partial class Program;

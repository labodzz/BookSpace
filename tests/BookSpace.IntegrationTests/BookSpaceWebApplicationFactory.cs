using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace BookSpace.IntegrationTests;

public sealed class BookSpaceWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:BookSpace"] = "Server=(localdb)\\MSSQLLocalDB;Database=BookSpace_Test;Trusted_Connection=True;TrustServerCertificate=True"
            }));
    }
}

using ArticleService.Models;
using Microsoft.EntityFrameworkCore;

namespace ArticleService.Data;

public class ArticleDbContextFactory
{
    private readonly IConfiguration _configuration;

    public ArticleDbContextFactory(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ArticleDbContext CreateForRegion(Region region)
    {
        var envKey = region switch
        {
            Region.Africa       => "DB_AFRICA",
            Region.Antarctica   => "DB_ANTARCTICA",
            Region.Asia         => "DB_ASIA",
            Region.Australia    => "DB_AUSTRALIA",
            Region.Europe       => "DB_EUROPE",
            Region.NorthAmerica => "DB_NORTHAMERICA",
            Region.SouthAmerica => "DB_SOUTHAMERICA",
            Region.Global       => "DB_GLOBAL",
            _ => throw new ArgumentOutOfRangeException(nameof(region))
        };

        var connectionString = Environment.GetEnvironmentVariable(envKey)
            ?? _configuration.GetConnectionString(envKey)
            ?? throw new InvalidOperationException(
                $"No connection string found for region {region}. Expected env var: {envKey}");

        var options = new DbContextOptionsBuilder<ArticleDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ArticleDbContext(options);
    }
}

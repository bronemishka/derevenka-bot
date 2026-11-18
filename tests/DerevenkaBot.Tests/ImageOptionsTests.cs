using DerevenkaBot.Core.Application;
using Microsoft.Extensions.Configuration;

namespace DerevenkaBot.Tests;

public sealed class ImageOptionsTests
{
    [Fact]
    public void InvalidImageCountFromConfigurationIdentifiesPropertyValueAndRange()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ImageGeneration:MaxInputImages"] = "10"
        }).Build();
        var options = configuration.GetSection("ImageGeneration").Get<ImageOptions>()!;
        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("MaxInputImages=10", error.Message);
        Assert.Contains("от 1 до 4", error.Message);
        options.MaxInputImages = 4;
        options.Validate();
    }

    [Fact]
    public void AllInvalidTechnicalLimitsAreReportedTogetherWithoutCredentials()
    {
        var options = new ImageOptions { MaxConcurrentJobs = 0, LeaseSeconds = 1, ReservationUsd = -0.25m,
            ApiKey = "private-api-key", SafetySalt = "private-safety-salt" };
        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("MaxConcurrentJobs=0", error.Message);
        Assert.Contains("LeaseSeconds=1", error.Message);
        Assert.Contains("ReservationUsd=-0.25", error.Message);
        Assert.DoesNotContain(options.ApiKey, error.Message);
        Assert.DoesNotContain(options.SafetySalt, error.Message);
    }

    [Fact]
    public void DisabledQuotasStillValidateTechnicalLimits()
    {
        var options = new ImageOptions { LimitsEnabled = false, DailyJobsPerUser = 0, UserDailyBudgetUsd = 0 };
        options.Validate();
        options.MaxInputImages = 10;
        Assert.Contains("MaxInputImages=10", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TestHarness.Core;

class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 1. Configuration
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        // 2. Logging
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            var logLevelString = config["TestHarness:LogLevel"] ?? "Information";
            var logLevel = Enum.Parse<LogLevel>(logLevelString);
            builder
                .AddConsole()
                .SetMinimumLevel(logLevel);
        });

        var logger = loggerFactory.CreateLogger<Program>();
        logger.LogInformation("Test Harness starting");

        // 3. Choose runner based on config
        var runnerType = config["TestHarness:Runner"] ?? "NUnit";

        ITestRunner runner = runnerType switch
        {
            "Reflection" => new SimpleTestRunner(
                                config,
                                loggerFactory.CreateLogger<SimpleTestRunner>()),
            "NUnit" => new NUnitEngineTestRunner(
                                config,
                                loggerFactory.CreateLogger<NUnitEngineTestRunner>())
        };

        // 4. Run tests
        var result = await runner.RunAsync();

        // 5. Generate report
        var reportLogger = loggerFactory.CreateLogger<JsonReportGenerator>();
        var reportGen = new JsonReportGenerator(config, reportLogger);
        await reportGen.WriteReportAsync(result);

        // 6. Exit code logic
        bool anyCritical = result.TestCases.Any(t => t.Outcome == TestOutcome.CRITICAL_ERROR);
        bool anyFail = result.TestCases.Any(t => t.Outcome == TestOutcome.FAIL);

        if (anyCritical)
        {
            logger.LogError("Test run finished with critical errors");
            return 2;
        }

        if (anyFail)
        {
            logger.LogWarning("Test run finished with failures");
            return 1;
        }

        logger.LogInformation("Test run finished successfully");
        return 0;
    }
}

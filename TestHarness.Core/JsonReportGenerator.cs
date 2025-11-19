using System;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace TestHarness.Core
{
    public class JsonReportGenerator
    {
        private readonly IConfiguration _config;
        private readonly ILogger<JsonReportGenerator> _logger;
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public JsonReportGenerator(IConfiguration config, ILogger<JsonReportGenerator> logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task WriteReportAsync(TestSuiteResult result)
        {
            var path = _config["TestHarness:ReportOutputPath"] ?? "reports/test-results.json";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var json = JsonSerializer.Serialize(result, _jsonOptions);

            await File.WriteAllTextAsync(path, json);

            _logger.LogInformation("Report written to {Path}", path);
        }
    }
}

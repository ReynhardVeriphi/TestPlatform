using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NUnit.Engine;

namespace TestHarness.Core
{
    public class NUnitEngineTestRunner : ITestRunner
    {
        private readonly IConfiguration _config;
        private readonly ILogger<NUnitEngineTestRunner> _logger;

        public NUnitEngineTestRunner(IConfiguration config, ILogger<NUnitEngineTestRunner> logger)
        {
            _config = config;
            _logger = logger;
        }

        public Task<TestSuiteResult> RunAsync()
        {
            var suite = new TestSuiteResult
            {
                StartedAt = DateTime.Now,
            };

            // 1) Try explicit assemblies configured
            var explicitAssemblies = GetExplicitAssemblies();

            string[] assemblies;

            if (explicitAssemblies.Length > 0)
            {
                assemblies = ResolveExplicitFiles(explicitAssemblies);

                if (assemblies.Length == 0)
                {
                    _logger.LogWarning("No files found from explicit TestHarness:TestAssemblies entries");
                }
            }
            else
            {
                // 2) Fallback: scan configured paths using pattern
                var assemblyPattern = _config["TestHarness:TestAssemblyPattern"] ?? "*.Tests.dll";

                var paths = GetConfiguredPaths();

                if (!paths.Any())
                {
                    _logger.LogWarning("No TestHarness:TestAssemblies or TestHarness:TestAssembliesPath configured");
                    suite.FinishedAt = DateTime.UtcNow;
                    return Task.FromResult(suite);
                }

                assemblies = FindAssembliesInPaths(paths, assemblyPattern);

                if (!assemblies.Any())
                {
                    _logger.LogWarning("No test assemblies found using pattern {Pattern} in configured paths", assemblyPattern);
                    suite.FinishedAt = DateTime.UtcNow;
                    return Task.FromResult(suite);
                }
            }

            _logger.LogInformation("Running NUnit tests for assemblies: {Assemblies}", string.Join(", ", assemblies));

            try
            {
                using var engine = TestEngineActivator.CreateInstance();
                var package = new TestPackage(assemblies);

                // Optional: configure work directory, etc.
                // package.Settings["WorkDirectory"] = AppContext.BaseDirectory;

                using var runner = engine.GetRunner(package);

                XmlNode resultXml = runner.Run(listener: null, filter: TestFilter.Empty);

                var results = ExtractResults(resultXml);
                suite.TestCases.AddRange(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error running NUnit tests");
                suite.TestCases.Add(new TestCaseResult
                {
                    TestName = "NUnitEngine.Run",
                    ClassName = "NUnitEngine",
                    Duration = TimeSpan.Zero,
                    Outcome = TestOutcome.CRITICAL_ERROR,
                    Message = ex.Message,
                    StackTrace = ex.StackTrace
                });
            }

            suite.FinishedAt = DateTime.UtcNow;

            _logger.LogInformation(
                "Finished NUnit test run: {Total} total, {Passed} passed, {Failed} failed, {Critical} critical",
                suite.TestCases.Count,
                suite.TestCases.Count(tc => tc.Outcome == TestOutcome.PASS),
                suite.TestCases.Count(tc => tc.Outcome == TestOutcome.FAIL),
                suite.TestCases.Count(tc => tc.Outcome == TestOutcome.CRITICAL_ERROR)
            );

            return Task.FromResult(suite);
        }

        // Read explicit assembly list from configuration (array or semicolon-separated string)
        private string[] GetExplicitAssemblies()
        {
            var explicitSection = _config.GetSection("TestHarness:TestAssemblies");
            var explicitAssemblies = explicitSection.Get<string[]>() ?? Array.Empty<string>();

            if (explicitAssemblies.Length == 0)
            {
                var single = _config["TestHarness:TestAssemblies"];
                if (!string.IsNullOrWhiteSpace(single))
                {
                    explicitAssemblies = single
                        .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(a => a.Trim())
                        .ToArray();
                }
            }

            return explicitAssemblies;
        }

        private string[] ResolveExplicitFiles(string[] explicitAssemblies)
        {
            return explicitAssemblies
                .Select(a => Path.GetFullPath(a))
                .Where(File.Exists)
                .ToArray();
        }

        // Read configured paths (array or semicolon-separated string)
        private List<string> GetConfiguredPaths()
        {
            var pathsSection = _config.GetSection("TestHarness:TestAssembliesPath");
            var paths = new List<string>();

            if (pathsSection.Exists())
            {
                var pathArray = pathsSection.Get<string[]>();
                if (pathArray != null && pathArray.Length > 0)
                {
                    paths.AddRange(pathArray.Where(p => !string.IsNullOrWhiteSpace(p)));
                }
                else
                {
                    var single = _config["TestHarness:TestAssembliesPath"];
                    if (!string.IsNullOrWhiteSpace(single))
                    {
                        paths.AddRange(single.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()));
                    }
                }
            }

            return paths;
        }

        private string[] FindAssembliesInPaths(IEnumerable<string> paths, string assemblyPattern)
        {
            var found = new List<string>();

            foreach (var p in paths)
            {
                try
                {
                    var full = Path.GetFullPath(p);

                    if (File.Exists(full) && string.Equals(Path.GetExtension(full), ".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(full);
                        continue;
                    }

                    if (!Directory.Exists(full))
                    {
                        _logger.LogWarning("Test assembly path does not exist: {Path}", full);
                        continue;
                    }

                    found.AddRange(Directory.GetFiles(full, assemblyPattern, SearchOption.TopDirectoryOnly));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enumerate test assemblies in {Path}", p);
                }
            }

            return found.Distinct().ToArray();
        }

        private List<TestCaseResult> ExtractResults(XmlNode root)
        {
            var results = new List<TestCaseResult>();

            if (root == null)
            {
                _logger.LogWarning("NUnit result XML is null");
                return results;
            }

            var testCaseNodes = root.SelectNodes("//test-case");
            if (testCaseNodes == null || testCaseNodes.Count == 0)
            {
                _logger.LogWarning("No <test-case> nodes found in NUnit result XML");
                return results;
            }

            foreach (XmlNode node in testCaseNodes)
            {
                var name = node.Attributes?["name"]?.Value ?? string.Empty;
                var fullName = node.Attributes?["fullname"]?.Value ?? name;
                var className = node.Attributes?["classname"]?.Value ?? string.Empty;
                var result = node.Attributes?["result"]?.Value ?? "Unknown";
                var durationStr = node.Attributes?["duration"]?.Value ?? "0";

                TimeSpan duration = TimeSpan.Zero;
                if (double.TryParse(durationStr,
                                    System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    out var seconds))
                {
                    duration = TimeSpan.FromSeconds(seconds);
                }

                string message = string.Empty;
                string? stackTrace = null;

                var failureNode = node.SelectSingleNode("failure");
                if (failureNode != null)
                {
                    var msgNode = failureNode.SelectSingleNode("message");
                    var stackNode = failureNode.SelectSingleNode("stack-trace");
                    if (msgNode != null) message = msgNode.InnerText.Trim();
                    if (stackNode != null) stackTrace = stackNode.InnerText.Trim();
                }

                results.Add(new TestCaseResult
                {
                    TestName = fullName,
                    ClassName = className,
                    Duration = duration,
                    Outcome = MapOutcome(result),
                    Message = message,
                    StackTrace = stackTrace
                });
            }

            return results;
        }

        private TestOutcome MapOutcome(string nunitResult)
        {
            return nunitResult switch
            {
                "Passed" => TestOutcome.PASS,
                "Failed" => TestOutcome.FAIL,
                "Skipped" => TestOutcome.FAIL,
                "Inconclusive" => TestOutcome.FAIL,
                _ => TestOutcome.CRITICAL_ERROR
            };
        }
    }
}

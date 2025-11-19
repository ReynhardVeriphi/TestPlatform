using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TestHarness.Core
{
    /**
     a full-featured test runner; a simple implementation that discovers and runs. Is not used by program at the moment
     */
    public class SimpleTestRunner : ITestRunner
    {
        private readonly IConfiguration _config;
        private readonly ILogger<SimpleTestRunner> _logger;

        public SimpleTestRunner(IConfiguration config, ILogger<SimpleTestRunner> logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task<TestSuiteResult> RunAsync()
        {
            var suite = new TestSuiteResult
            {
                StartedAt = DateTime.UtcNow
            };

            _logger.LogInformation("Starting test run");

            // Where to look for test assemblies (configurable)
            var assemblyPattern = _config["TestHarness:TestAssemblyPattern"] ?? "*.Tests.dll";

            // Support multiple configured paths:
            // - JSON array: "TestAssembliesPath": ["pathA","pathB"]
            // - Semicolon-separated string: "pathA;pathB"
            // - Single string: "pathA"
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

            if (!paths.Any())
            {
                // fallback to current base directory
                paths.Add(AppContext.BaseDirectory);
            }

            var assemblyFilesList = new List<string>();

            foreach (var p in paths)
            {
                try
                {
                    var full = Path.GetFullPath(p);
                    if (!Directory.Exists(full))
                    {
                        _logger.LogWarning("Test assembly path does not exist: {Path}", full);
                        continue;
                    }

                    // Use TopDirectoryOnly by default; change to AllDirectories if you want recursive search
                    assemblyFilesList.AddRange(Directory.GetFiles(full, assemblyPattern, SearchOption.TopDirectoryOnly));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enumerate files in {Path}", p);
                }
            }

            var assemblyFiles = assemblyFilesList.Distinct().ToArray();

            if (!assemblyFiles.Any())
            {
                _logger.LogInformation("No test assemblies found using pattern {Pattern} in configured paths", assemblyPattern);
            }

            var discoveredMethods = new List<(Assembly Assembly, Type Type, MethodInfo Method, string Framework)>();

            // Keep load contexts alive while reflecting to avoid GC/unloading issues
            var loadContexts = new List<AssemblyLoadContext>();

            foreach (var file in assemblyFiles)
            {
                Assembly asm;
                TestAssemblyLoadContext? alc = null;
                try
                {
                    // Use a custom AssemblyLoadContext so dependencies (like nunit.framework) are resolved
                    // from the test assembly folder using AssemblyDependencyResolver.
                    var full = Path.GetFullPath(file);
                    alc = new TestAssemblyLoadContext(full);
                    asm = alc.LoadFromAssemblyPath(full);
                    loadContexts.Add(alc);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load assembly {File}", file);
                    alc?.Unload();
                    continue;
                }

                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException rtle)
                {
                    types = rtle.Types.Where(t => t != null).ToArray()!;
                    _logger.LogWarning(rtle, "Some types in {Assembly} failed to load; continuing with those that did.", asm.FullName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enumerate types for {Assembly}", asm.FullName);
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null) continue;

                    foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        // Detect xUnit and NUnit attributes by attribute type full name (no compile-time dependency)
                        // Wrap attribute inspection in try/catch because reading CustomAttributeData can trigger
                        // loading of referenced assemblies; with the custom ALC dependencies should resolve,
                        // but protect against any remaining failures.
                        IEnumerable<CustomAttributeData> attrs;
                        try
                        {
                            attrs = method.GetCustomAttributesData();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to read custom attributes for {Type}.{Method} in {Assembly}", type.FullName, method.Name, asm.FullName);
                            continue;
                        }

                        foreach (var ad in attrs)
                        {
                            var attrName = ad.AttributeType.FullName ?? string.Empty;

                            if (attrName == "Xunit.FactAttribute" || attrName == "Xunit.TheoryAttribute")
                            {
                                discoveredMethods.Add((asm, type, method, "xUnit"));
                                break;
                            }

                            if (attrName == "NUnit.Framework.TestAttribute" || attrName == "NUnit.Framework.TestCaseAttribute")
                            {
                                discoveredMethods.Add((asm, type, method, "NUnit"));
                                break;
                            }
                        }
                    }
                }
            }

            if (!discoveredMethods.Any())
            {
                _logger.LogInformation("No xUnit/NUnit test methods discovered in configured paths");
            }

            foreach (var m in discoveredMethods)
            {
                var caseResult = new TestCaseResult
                {
                    TestName = m.Method.Name,
                    ClassName = m.Type.FullName ?? m.Type.Name,
                    Duration = TimeSpan.Zero,
                    Outcome = TestOutcome.FAIL,
                    Message = string.Empty
                };

                var sw = Stopwatch.StartNew();

                try
                {
                    // For parameterized tests (methods with parameters) we try to support simple cases:
                    var parameters = m.Method.GetParameters();
                    if (parameters.Length > 0)
                    {
                        // For now we do not try to synthesize parameter values for theories/testcases.
                        caseResult.Outcome = TestOutcome.FAIL;
                        caseResult.Message = "Parameterized tests are not executed by this runner.";
                        _logger.LogInformation("Skipping parameterized test {Type}.{Method}", m.Type.FullName, m.Method.Name);
                    }
                    else
                    {
                        object? instance = null;
                        if (!m.Method.IsStatic)
                        {
                            try
                            {
                                instance = Activator.CreateInstance(m.Type);
                            }
                            catch (Exception ex)
                            {
                                caseResult.Outcome = TestOutcome.CRITICAL_ERROR;
                                caseResult.Message = $"Failed to create test class instance: {ex.Message}";
                                _logger.LogError(ex, "Failed to create instance of {Type} for test {Method}", m.Type.FullName, m.Method.Name);
                                suite.TestCases.Add(caseResult);
                                sw.Stop();
                                caseResult.Duration = sw.Elapsed;
                                continue;
                            }
                        }

                        var invokeResult = m.Method.Invoke(instance, null);

                        // Support async test methods that return Task or Task<T>
                        if (invokeResult is Task task)
                        {
                            try
                            {
                                await task.ConfigureAwait(false);
                                caseResult.Outcome = TestOutcome.PASS;
                            }
                            catch (Exception ex)
                            {
                                // Unwrap TargetInvocationException if present
                                var real = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                                caseResult.Outcome = TestOutcome.FAIL;
                                caseResult.Message = real.Message;
                                _logger.LogWarning(real, "Test {Type}.{Method} failed", m.Type.FullName, m.Method.Name);
                            }
                        }
                        else
                        {
                            // Synchronous method completed successfully
                            caseResult.Outcome = TestOutcome.PASS;
                        }
                    }
                }
                catch (TargetInvocationException tie) // exceptions thrown by the invoked method
                {
                    var real = tie.InnerException ?? tie;
                    caseResult.Outcome = TestOutcome.FAIL;
                    caseResult.Message = real.Message;
                    caseResult.StackTrace = real.StackTrace;
                    _logger.LogWarning(real, "Test {Type}.{Method} failed", m.Type.FullName, m.Method.Name);
                }
                catch (Exception ex)
                {
                    caseResult.Outcome = TestOutcome.CRITICAL_ERROR;
                    caseResult.Message = ex.Message;
                    caseResult.StackTrace = ex.StackTrace;
                    _logger.LogError(ex, "Critical error while running test {Type}.{Method}", m.Type.FullName, m.Method.Name);
                }
                finally
                {
                    sw.Stop();
                    caseResult.Duration = sw.Elapsed;
                    suite.TestCases.Add(caseResult);
                }
            }

            suite.FinishedAt = DateTime.UtcNow;

            _logger.LogInformation("Finished test run: {Count} tests discovered, {Passed} passed",
                suite.TestCases.Count, suite.TestCases.Count(tc => tc.Outcome == TestOutcome.PASS));

            return suite;
        }

        // Custom AssemblyLoadContext that resolves dependencies relative to the test assembly location
        private sealed class TestAssemblyLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver _resolver;

            public TestAssemblyLoadContext(string mainAssemblyPath) : base(isCollectible: true)
            {
                _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
            }

            protected override Assembly? Load(AssemblyName assemblyName)
            {
                var path = _resolver.ResolveAssemblyToPath(assemblyName);
                if (path != null)
                {
                    try
                    {
                        return LoadFromAssemblyPath(path);
                    }
                    catch
                    {
                        // let default resolve or fail
                        return null;
                    }
                }

                return null;
            }
        }
    }
}

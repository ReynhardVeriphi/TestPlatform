# TestHarness

Lightweight test harness that discovers and runs tests from compiled test assemblies and writes a JSON report.

## Prerequisites

- .NET 8 SDK installed: https://dotnet.microsoft.com/en-us/download
- Git (to clone)

## Clone

```bash
git clone https://github.com/ReynhardVeriphi/TestPlatform.git
cd TestPlatform
```

## Restore & build

```bash
dotnet restore
dotnet build
```

## Configure

The harness reads `appsettings.json` from the CLI project's output folder (the one under `TestHarness.Cli`). You can edit `TestHarness.Cli\appsettings.json` in the repository before running or override settings with environment variables or command-line arguments.

Minimum recommended keys under `TestHarness`:

- `Runner` - (optional) which runner to use. Example: `"NUnit"` or `"Reflection"` (the reflection-based `SimpleTestRunner`).
- `TestAssembliesPath` - array (or semicolon-separated string) of directories containing compiled test DLLs, or explicit DLL paths using `TestAssemblies` (see examples below).
- `TestAssemblyPattern` - file search pattern (default: `*.Tests.dll`).
- `ReportOutputPath` - path to write the JSON report (relative to the CLI working directory).

Example `appsettings.json` (place under `TestHarness.Cli` and set its file properties to "Copy if newer" if desired):

```json
{
  "TestHarness": {
    "Runner": "NUnit",
    "TestAssembliesPath": [
      "C:/path/to/external/tests/ProjectA/bin/Debug/net8.0",
      "C:/path/to/external/tests/ProjectB/bin/Debug/net8.0"
    ],
    "TestAssemblyPattern": "*.Tests.dll",
    "ReportOutputPath": "reports/test-results.json"
  },
  "TestHarness:LogLevel": "Information"
}
```

Alternative: explicitly list DLLs:

```json
{
  "TestHarness": {
    "TestAssemblies": [
      "C:/.../ProjectA/bin/Debug/net8.0/ProjectA.Tests.dll",
      "C:/.../ProjectB/bin/Debug/net8.0/ProjectB.Tests.dll"
    ],
    "ReportOutputPath": "reports/test-results.json"
  }
}
```

Notes:
- Use forward slashes or escape backslashes in JSON strings.
- Do not include comments in JSON files.
- The harness loads compiled assemblies: ensure test projects are built and compiled to a compatible target framework (preferably `net8.0`).

## Run

From repository root you can run the CLI project:

```bash
dotnet run --project TestHarness.Cli
```

Or run the built executable in `TestHarness.Cli/bin/Debug/net8.0` after building.

You can override configuration with environment variables (use `TestHarness__TestAssembliesPath` for path) or command-line args (because the app uses `AddCommandLine`). Example:

```bash
dotnet run --project TestHarness.Cli -- --TestHarness:TestAssembliesPath "C:/path/to/tests" --TestHarness:TestAssemblyPattern "*.Tests.dll"
```

## Troubleshooting

- "Could not load file or assembly 'nunit.framework'" — ensure the test assembly output folder contains `nunit.framework.dll` (NuGet package restore and build normally place dependencies alongside the test DLL). Prefer building tests to `net8.0`.
- JSON parse errors — remove comments and escape backslashes in `appsettings.json`.
- If no tests are found, verify `TestAssembliesPath` points to the compiled `bin/<cfg>/<tfm>` folder and that files match the pattern.


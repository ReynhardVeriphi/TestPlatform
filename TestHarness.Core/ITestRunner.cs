using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TestHarness.Core
{
    public interface ITestRunner
    {
        Task<TestSuiteResult> RunAsync();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TestHarness.Core
{
    public class TestSuiteResult
    {
        public DateTime StartedAt { get; set; }
        public DateTime FinishedAt { get; set; }
        public List<TestCaseResult> TestCases { get; set; } = new();
    }
}

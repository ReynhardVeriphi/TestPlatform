using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TestHarness.Core
{
   public class TestCaseResult
    {
        public string TestName { get; set; } = "";
        public string ClassName { get; set; } = "";
        public TimeSpan Duration { get; set; }
        public TestOutcome Outcome { get; set; }
        public string Message { get; set; } = "";
        public string? StackTrace { get; set; }
    }
}

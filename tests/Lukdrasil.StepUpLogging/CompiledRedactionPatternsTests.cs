using System;
using System.Text.RegularExpressions;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests
{
    public class CompiledRedactionPatternsTests
    {
        private static Regex Compile(string pattern) =>
            new(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

        // Tiny timeout for the catastrophic-backtracking cases: forces RegexMatchTimeoutException in
        // ~1ms instead of burning ~100ms of CPU, which could starve timing-sensitive tests in parallel.
        private static Regex CompileFast(string pattern) =>
            new(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(1));

        [Fact(DisplayName = "Redact_ReturnsSentinel_WhenPatternTimesOut")]
        public void Redact_ReturnsSentinel_WhenPatternTimesOut()
        {
            // Catastrophic backtracking against a long non-matching input blows past the 100ms timeout.
            var secret = new string('a', 40) + "!";
            var patterns = new CompiledRedactionPatterns(new[] { CompileFast("(a+)+$") });

            var result = patterns.Redact(secret);

            Assert.Equal("[REDACTION-ERROR]", result);
            Assert.DoesNotContain(new string('a', 40), result);
        }

        [Fact(DisplayName = "Redact_FailsPerPatternNotGlobally_WhenOnePatternThrows")]
        public void Redact_FailsPerPatternNotGlobally_WhenOnePatternThrows()
        {
            // A global try/catch would return the raw input on the throwing pattern, leaking the
            // literal secret the second pattern is meant to mask. Per-pattern fail-closed must not.
            var input = new string('a', 40) + "! keepsecret";
            var patterns = new CompiledRedactionPatterns(new[]
            {
                CompileFast("(a+)+$"),
                CompileFast("keepsecret"),
            });

            var result = patterns.Redact(input);

            Assert.Equal("[REDACTION-ERROR]", result);
            Assert.DoesNotContain("keepsecret", result);
            Assert.DoesNotContain(new string('a', 40), result);
        }

        [Fact(DisplayName = "Redact_ReturnsInput_WhenEmpty")]
        public void Redact_ReturnsInput_WhenEmpty()
        {
            var patterns = new CompiledRedactionPatterns(new[] { Compile("secret") });

            Assert.Equal(string.Empty, patterns.Redact(string.Empty));
        }

        [Fact(DisplayName = "Redact_ReturnsInput_WhenNoPatterns")]
        public void Redact_ReturnsInput_WhenNoPatterns()
        {
            var patterns = new CompiledRedactionPatterns(Array.Empty<Regex>());

            Assert.Equal("nothing to redact", patterns.Redact("nothing to redact"));
        }

        [Fact(DisplayName = "Redact_ReplacesMatch_WhenPatternSucceeds")]
        public void Redact_ReplacesMatch_WhenPatternSucceeds()
        {
            var patterns = new CompiledRedactionPatterns(new[] { Compile("password=[^&]+") });

            var result = patterns.Redact("user=bob&password=hunter2&x=1");

            Assert.Equal("user=bob&[REDACTED]&x=1", result);
        }
    }
}

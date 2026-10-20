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

        [Fact(DisplayName = "CompilePattern_NonBacktracking_MatchesInLinearTime_WithoutTimeout")]
        public void CompilePattern_NonBacktracking_MatchesInLinearTime_WithoutTimeout()
        {
            // (a+)+$ against a long non-matching input would blow past the 100ms timeout under the old
            // backtracking engine and yield the fail-closed sentinel. NonBacktracking evaluates in
            // linear time, finds no match, and returns the input verbatim.
            var input = new string('a', 5000) + "!";
            var patterns = new CompiledRedactionPatterns(new[] { StepUpLoggingExtensions.CompilePattern("(a+)+$") });

            var result = patterns.Redact(input);

            Assert.Equal(input, result);
            Assert.NotEqual("[REDACTION-ERROR]", result);
        }

        [Fact(DisplayName = "CompilePattern_Lookahead_CompilesViaCompiledFallback_AndRedacts")]
        public void CompilePattern_Lookahead_CompilesViaCompiledFallback_AndRedacts()
        {
            // A lookahead is unsupported by NonBacktracking (throws NotSupportedException at construction)
            // and must land on the Compiled fallback rather than propagate the exception.
            var regex = StepUpLoggingExtensions.CompilePattern("(?=secret)secret");
            var patterns = new CompiledRedactionPatterns(new[] { regex });

            var result = patterns.Redact("my secret here");

            Assert.Equal("my [REDACTED] here", result);
        }

        private const string Uuid = "3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b";
        private const string UuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

        [Fact]
        public void RedactProperty_ExemptName_ReturnsValueUnchanged()
        {
            var patterns = new CompiledRedactionPatterns([Compile(UuidPattern)], ["Jti"]);

            Assert.Equal("onrtac:" + Uuid, patterns.RedactProperty("Jti", "onrtac:" + Uuid));
        }

        [Fact]
        public void RedactProperty_OtherName_IsRedacted()
        {
            var patterns = new CompiledRedactionPatterns([Compile(UuidPattern)], ["Jti"]);

            Assert.Equal("[REDACTED]", patterns.RedactProperty("UserId", Uuid));
        }

        [Fact]
        public void RedactProperty_NameDiffersInCase_IsRedacted()
        {
            var patterns = new CompiledRedactionPatterns([Compile(UuidPattern)], ["Jti"]);

            Assert.Equal("onrtac:[REDACTED]", patterns.RedactProperty("jti", "onrtac:" + Uuid));
        }

        [Fact]
        public void ExemptProperties_DropsBlankEntries()
        {
            var patterns = new CompiledRedactionPatterns([Compile(UuidPattern)], ["Jti", " ", ""]);

            Assert.Equal(["Jti"], patterns.ExemptProperties);
        }

        [Fact]
        public void Redact_ReturnsSameInstance_WhenNoPatternMatches()
        {
            var patterns = new CompiledRedactionPatterns(new[] { Compile("password=[^&]+"), Compile(@"token/[^/]+") });
            var input = "/api/orders/8f3a2c1e/items/42?page=2";

            Assert.Same(input, patterns.Redact(input));
        }
    }
}

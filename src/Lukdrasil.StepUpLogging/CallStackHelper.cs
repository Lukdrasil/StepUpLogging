using System;
using System.Linq;
using Serilog.Core;

namespace Lukdrasil.StepUpLogging
{
    internal static class CallStackHelper
    {
        public static ILogEventEnricher? CreateCallStackEnricher()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? t = null;
                    try
                    {
                        t = asm.GetType("Serilog.Enrichers.CallStack.CallStackEnricher") ?? asm.GetType("Serilog.Enrichers.CallStack.StackTraceEnricher");
                        if (t == null)
                        {
                            t = asm.GetTypes().FirstOrDefault(x => (x.Name.IndexOf("CallStack", StringComparison.OrdinalIgnoreCase) >= 0
                                || x.Name.IndexOf("StackTrace", StringComparison.OrdinalIgnoreCase) >= 0)
                                && typeof(ILogEventEnricher).IsAssignableFrom(x));
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    if (t != null && typeof(ILogEventEnricher).IsAssignableFrom(t))
                    {
                        try
                        {
                            return (ILogEventEnricher?)Activator.CreateInstance(t);
                        }
                        catch
                        {
                            // ignore and continue
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }
    }
}

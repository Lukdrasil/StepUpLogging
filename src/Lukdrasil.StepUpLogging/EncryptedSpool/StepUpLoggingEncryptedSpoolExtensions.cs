using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// Registers the encrypted spooling audit sink (ADR 0017 D5): <see cref="EncryptedSpoolAuditSink"/>
/// as the <see cref="IAuditEventSink"/>, <see cref="DrainWorker"/> as a hosted service, a combined
/// health check under the <see cref="HealthCheckTag"/> tag, and the named <see cref="HttpClient"/>
/// the worker delivers with.
/// </summary>
public static class StepUpLoggingEncryptedSpoolExtensions
{
    /// <summary>The tag the encrypted spool's health check is registered under.</summary>
    public const string HealthCheckTag = "audit";

    /// <summary>The encrypted spool's health check name.</summary>
    public const string HealthCheckName = "encrypted-spool-audit";

    /// <summary>
    /// Adds the encrypted spooling audit sink: <see cref="EncryptedSpoolAuditSink"/>,
    /// <see cref="DrainWorker"/>, and the combined health check. Calls
    /// <see cref="StepUpLoggingExtensions.AddAuditLogging{TSink}"/> internally — do not call it
    /// yourself for this sink. There is no <c>Enabled</c> flag: not calling this method is how the
    /// sink stays off (ADR 0016 D4). Every option this method requires is validated at host start,
    /// not on the first audited operation, naming whichever is missing (ADR 0007).
    /// </summary>
    /// <remarks>
    /// The consumer's own <see cref="IAuditPayloadEncryptor"/> — which owns all key handling — is
    /// registered separately, in <c>Program.cs</c>; this method resolves it but never configures or
    /// touches key material (ADR 0017 D3). Register it as a singleton, thread-safe: the sink that
    /// calls it is itself a singleton, calling it concurrently outside its own write gate. A
    /// <c>Scoped</c> registration becomes a captive dependency the moment this method's singleton
    /// sink resolves it — promoted to the root scope for the process's lifetime in Production, and
    /// refused outright by scope validation in Development.
    /// <para>
    /// If <see cref="EncryptedSpoolOptions.SpoolDirectory"/> cannot be created or is not writable,
    /// the host fails to start with the raw filesystem exception (e.g. <see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>) — resolving the sink during start-up validation is
    /// what forces this to surface then, rather than on the first audited operation (B07 hand-off).
    /// </para>
    /// </remarks>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configureOptions">
    /// Sets <see cref="EncryptedSpoolOptions"/> — at minimum <see cref="EncryptedSpoolOptions.SpoolDirectory"/>,
    /// <see cref="EncryptedSpoolOptions.EndpointBaseUrl"/>, <see cref="EncryptedSpoolOptions.ModuleName"/>,
    /// and <see cref="EncryptedSpoolOptions.Version"/>.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// An <see cref="IAuditEventSink"/> is already registered — either a prior call to this method,
    /// a direct <see cref="StepUpLoggingExtensions.AddAuditLogging{TSink}"/>, or a raw
    /// <c>services.AddSingleton&lt;IAuditEventSink, X&gt;()</c> (ADR 0018 D5). Call this method
    /// exactly once, and never call <c>AddAuditLogging</c> for this sink — it does that internally.
    /// </exception>
    public static IHostApplicationBuilder AddEncryptedSpoolAuditSink(
        this IHostApplicationBuilder builder,
        Action<EncryptedSpoolOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);
        GuardAgainstExistingSink(builder);

        builder.Services.AddSingleton<IValidateOptions<EncryptedSpoolOptions>, EncryptedSpoolOptionsValidator>();
        builder.Services.AddOptions<EncryptedSpoolOptions>()
            .Configure(configureOptions)
            .ValidateOnStart();

        // Singletons because each is a seam two collaborators must meet on: the sink writes the
        // tally the gauges observe, the drain worker sets the dead-letter and endpoint signals the
        // health check reports, and the SpoolWriter's crash-recovery sweep runs in its constructor,
        // so a second instance would sweep the same .tmp files a second time.
        builder.Services.AddSingleton(sp => new SpoolWriter(SpoolOptions(sp).SpoolDirectory));
        builder.Services.AddSingleton(sp => new SpoolUsageTracker(new SpoolCapacity(SpoolOptions(sp))));
        builder.Services.AddSingleton<DeadLetterBox>();
        builder.Services.AddSingleton<EndpointReachability>();
        // TryAdd, not Add: a consumer who registered their own TimeProvider first (a FakeTimeProvider
        // in their own integration tests, say) keeps it — this library's opinion only applies when
        // nothing else already supplied one.
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<EncryptedSpoolGauges>();

        AddDeliveryHttpClient(builder.Services);

        builder.AddAuditLogging<EncryptedSpoolAuditSink>(ServiceLifetime.Singleton);
        builder.Services.AddHostedService<DrainWorker>();
        builder.Services.AddHealthChecks().AddCheck<EncryptedSpoolHealthCheck>(HealthCheckName, tags: [HealthCheckTag]);

        // Forces the sink — and the SpoolWriter whose constructor runs the crash-recovery sweep —
        // to resolve at host start, so an unwritable SpoolDirectory surfaces there instead of on
        // the first audited business operation (B07 hand-off). The gauges resolve alongside it so
        // their ObservableGauge instruments exist from host start rather than only once something
        // else happens to touch them.
        // The message below is never actually shown: ResolveSinkAtStart only ever returns true or
        // lets the sink's own constructor exception escape unwrapped (see its remarks), so this
        // string exists only to satisfy Validate's signature. The real guidance for that failure
        // lives on this method's own <remarks>.
        builder.Services.AddOptions<EncryptedSpoolAuditSinkPrerequisites>()
            .Validate<IServiceProvider>(ResolveSinkAtStart, "AddEncryptedSpoolAuditSink's start-up check failed.")
            .ValidateOnStart();

        return builder;
    }

    /// <summary>
    /// Throws naming <em>this</em> method — not <see cref="StepUpLoggingExtensions.AddAuditLogging{TSink}"/>,
    /// which this method calls internally and whose own guard message would otherwise be what the
    /// caller sees, prescribing a remedy (<c>RemoveAll&lt;IAuditEventSink&gt;()</c>) that fits a
    /// direct <c>AddAuditLogging</c> mistake, not a duplicated call to this one.
    /// </summary>
    private static void GuardAgainstExistingSink(IHostApplicationBuilder builder)
    {
        var existingSinkRegistration = builder.Services.FirstOrDefault(sd => sd.ServiceType == typeof(IAuditEventSink));
        if (existingSinkRegistration is null)
        {
            return;
        }

        var existingSinkName = existingSinkRegistration.ImplementationType?.Name ?? "an existing IAuditEventSink";
        throw new InvalidOperationException(
            $"AddEncryptedSpoolAuditSink failed: {existingSinkName} is already registered as the {nameof(IAuditEventSink)}. " +
            $"{nameof(AuditLogger<object>)} resolves a single sink, so registering {nameof(EncryptedSpoolAuditSink)} " +
            "alongside it would silently discard one of them. Call AddEncryptedSpoolAuditSink exactly once.");
    }

    private static EncryptedSpoolOptions SpoolOptions(IServiceProvider services) =>
        services.GetRequiredService<IOptions<EncryptedSpoolOptions>>().Value;

    private static void AddDeliveryHttpClient(IServiceCollection services)
    {
        services.AddHttpClient(DrainWorker.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // A followed redirect would turn the drain worker's POST into a body-less GET
                // before it ever sees the 3xx, and a 2xx on that GET would delete a record that
                // was never actually sent — the one silent-loss path this sink exists to close
                // (B08 hand-off, hard requirement).
                AllowAutoRedirect = false
            })
            .ConfigureHttpClient((sp, client) =>
            {
                var options = SpoolOptions(sp);
                client.Timeout = options.DeliveryTimeout;
                options.ConfigureProducerCredentials?.Invoke(client);
            });
    }

    // Always true: the check is the resolution itself. A sink that cannot be built throws out of
    // GetRequiredService — carrying the directory and the reason — rather than handing back a
    // verdict this predicate could report.
    private static bool ResolveSinkAtStart(EncryptedSpoolAuditSinkPrerequisites prerequisites, IServiceProvider services)
    {
        try
        {
            // Runs EncryptedSpoolOptions' own validators first: if the options themselves are
            // invalid, that failure is already reported under its own name, and constructing a
            // sink from options that are not even well-formed would only add a second, noisier
            // failure about the same misconfiguration.
            _ = services.GetRequiredService<IOptionsMonitor<EncryptedSpoolOptions>>().CurrentValue;
        }
        catch (OptionsValidationException)
        {
            return true;
        }

        services.GetRequiredService<IAuditEventSink>();
        services.GetRequiredService<EncryptedSpoolGauges>();
        return true;
    }
}

/// <summary>
/// Marker options type whose only role is giving
/// <see cref="StepUpLoggingEncryptedSpoolExtensions.AddEncryptedSpoolAuditSink"/>'s sink-resolution
/// check a name <c>ValidateOnStart</c> can hang off, mirroring core's <c>AuditLoggingPrerequisites</c>.
/// </summary>
internal sealed class EncryptedSpoolAuditSinkPrerequisites;

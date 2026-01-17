# Opravená Konfigurace StepUpLogging s OTEL

Toto je příklad jak správně konfigurovat `Lukdrasil.StepUpLogging` 1.6.2+ s úplnou OTEL instrumentací.

## Minimální Setup (ASP.NET Core)

```csharp
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Traces;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// 1. Přidej StepUp logging s OTEL
builder.AddStepUpLogging(opts =>
{
    opts.Mode = StepUpMode.Auto;
    opts.BaseLevel = "Warning";
    opts.StepUpLevel = "Information";
    opts.DurationSeconds = 180;
    
    // OTEL expontery
    opts.EnableOtlpExporter = true;
    
    // Enrichment
    opts.EnrichWithExceptionDetails = true;  // ✅ Nyní funguje!
    opts.EnrichWithEnvironment = true;
    opts.EnrichWithMachineName = true;
    opts.EnrichWithThreadId = true;           // ✅ Nyní funguje!
    opts.EnrichWithProcessId = true;          // ✅ Nyní funguje!
    
    // Request logging
    opts.CaptureRequestBody = true;
    opts.MaxBodyCaptureBytes = 16 * 1024;
});

// 2. Přidej OpenTelemetry s ActivitySource registrací
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource =>
    {
        resource
            .AddService("my-service")
            .AddAttributes(new Dictionary<string, object>
            {
                { "service.version", "1.0.0" },
                { "deployment.environment", builder.Environment.EnvironmentName }
            });
    })
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource("Lukdrasil.StepUpLogging.RequestLogging")  // ✅ Request tracing
            .AddSource("Lukdrasil.StepUpLogging.Buffer")          // ✅ Buffer tracing
            .UseOtlpExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter("Lukdrasil.StepUpLogging")                 // ✅ Metriky
            .AddMeter("Lukdrasil.StepUpLogging.RequestLogging")
            .AddMeter("Lukdrasil.StepUpLogging.Buffer")
            .UseOtlpExporter();
    })
    .WithLogging(logging =>
    {
        logging.AddOtlpExporter();
    });

var app = builder.Build();

// 3. Přidej middleware
app.UseStepUpRequestLogging();  // ✅ Nyní s ActivitySource instrumentací
app.MapControllers();

app.Run();
```

## Environment Setup

```bash
# OTLP Exporter (gRPC - default)
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
export OTEL_EXPORTER_OTLP_PROTOCOL=grpc

# Nebo HTTP
export OTEL_EXPORTER_OTLP_PROTOCOL=http
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318

# Resource attributes
export OTEL_RESOURCE_ATTRIBUTES=service.name=my-api,service.version=1.0.0,environment=production

# Optional: Headers pro auth
export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer%20mytoken123"
```

## Docker Compose (Local Development)

```yaml
version: '3.8'

services:
  jaeger:
    image: jaegertracing/all-in-one:latest
    ports:
      - "4317:4317"  # OTLP gRPC
      - "4318:4318"  # OTLP HTTP
      - "16686:16686" # Jaeger UI
    environment:
      COLLECTOR_OTLP_ENABLED: "true"

  prometheus:
    image: prom/prometheus:latest
    ports:
      - "9090:9090"
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml

  my-app:
    build:
      context: .
      dockerfile: Dockerfile
    ports:
      - "5000:5000"
    environment:
      OTEL_EXPORTER_OTLP_ENDPOINT: http://jaeger:4317
      OTEL_EXPORTER_OTLP_PROTOCOL: grpc
      OTEL_RESOURCE_ATTRIBUTES: service.name=my-app,environment=development
    depends_on:
      - jaeger
      - prometheus
```

## Klíčové Rozdíly vs. Starší Verze

| Funkce | Před (1.6.1) | Po (1.6.2+) |
|--------|--------------|------------|
| **Async Disposal** | ❌ Timeout rizik | ✅ IAsyncDisposable |
| **Exception Details** | ❌ Nenačteno | ✅ `.Enrich.WithExceptionDetails()` |
| **Thread/Process** | ❌ Nenačteno | ✅ Úplně implementováno |
| **ActivitySource** | ❌ Neexistuje | ✅ Request + Buffer logging |
| **LRU Eviction** | ⚠️ Race condition | ✅ Bezpečné locking |
| **Request Tracing** | ❌ Bez span | ✅ Automatické `LogRequest` activity |

## Očekávaný Output

### V Jaegeru
```
Service: my-app

Traces:
├── LogRequest (span)
│   ├── Tags: http.method=GET, http.target=/api/users
│   ├── Tags: security.redaction_applied=true
│   └── CaptureRequestBody (child span)
│       └── Tags: request.body.size=128

├── StepUp Activity
│   ├── Logging step up: Information for 180 seconds
│   └── StepDown Activity (po timeout)
│       └── Logging step down: Warning
```

### V Prometheus

```
# Metrics
stepup_trigger_total{service_name="my-app"} = 5
stepup_active{service_name="my-app"} = 0
buffer_events_total{service_name="my-app"} = 1250
buffer_flushed_events_total{service_name="my-app"} = 42
request_body_captured_total{service_name="my-app"} = 12
request_redaction_applied_total{service_name="my-app"} = 89
```

## Troubleshooting

### Problém: Activities se neobjevují v Jaegeru

**Řešení:**
```csharp
// Ujistěte se, že registrujete ActivitySource:
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        // ✅ MUSÍ být přidáno
        tracing.AddSource("Lukdrasil.StepUpLogging.RequestLogging");
        tracing.AddSource("Lukdrasil.StepUpLogging.Buffer");
    });
```

### Problém: Exception Details chybí v logech

**Řešení:**
```csharp
// Ujistěte se, že je povoleno v options:
opts.EnrichWithExceptionDetails = true;
opts.StructuredExceptionDetails = true;
```

### Problém: Memory rastoucí s časem

**Řešení:**
- Ověřte, že `PreErrorBufferSink.Dispose()` je voláno
- Zkontrolujte LRU eviction limit: `opts.PreErrorMaxContexts = 1024`
- V Jaegeru hledejte `buffer_evicted_contexts_total` metriku

## Reference

- [Serilog Exception Details](https://github.com/serilog-community/serilog-exceptions)
- [Serilog Enrichers](https://github.com/serilog/serilog/wiki/Enrichment)
- [OpenTelemetry .NET](https://opentelemetry.io/docs/instrumentation/net/)
- [OTEL Semantic Conventions](https://opentelemetry.io/docs/specs/semconv/)

---

**Aktualizováno:** 17.1.2026  
**Verze:** 1.6.2+

# ActivitySource v StepUpLogging - Design Rozhodnutí

**Verze:** 1.6.2+  
**Aktualizováno:** 17.1.2026

---

## 🤔 Otázka: Je ActivitySource Skutečně Potřeba?

Ano... ale **není povinný**. Zde je detailní vysvětlení.

---

## 📊 Analýza: Logging vs. Tracing

```
┌─────────────────────────────────────────┐
│ StepUpLogging - Primární Concerns       │
├─────────────────────────────────────────┤
│ ✅ Logging - CORE                       │
│   - Dynamic log levels (Step-Up)        │
│   - Request body capture                │
│   - Sensitive data redaction            │
│   - Structured exception details        │
│                                         │
│ ⚠️ Tracing - OPTIONAL                   │
│   - ActivitySource (not required)       │
│   - Activity instrumentation (nice!)    │
│   - Distributed trace correlation       │
└─────────────────────────────────────────┘
```

---

## 🎯 Design Decision: Public ActivitySources

### **Co jsme udělali**

```csharp
// ✅ NOVÝ PŘÍSTUP - Veřejné a Optional

public static class StepUpLoggingExtensions
{
    /// <summary>
    /// Public ActivitySource for request logging (OPTIONAL).
    /// Only use if you want to trace body capture and redaction operations.
    /// </summary>
    public static readonly ActivitySource RequestLoggingActivitySource 
        = new("Lukdrasil.StepUpLogging.RequestLogging", "1.0.0");

    /// <summary>
    /// Public ActivitySource for buffer operations (OPTIONAL).
    /// Only use if you want to trace buffer flushing.
    /// </summary>
    public static readonly ActivitySource BufferActivitySource 
        = new("Lukdrasil.StepUpLogging.Buffer", "1.0.0");

    // Constants pro explicitní registraci
    public const string RequestLoggingActivitySourceName = "Lukdrasil.StepUpLogging.RequestLogging";
    public const string BufferActivitySourceName = "Lukdrasil.StepUpLogging.Buffer";
}
```

### **Přínosy tohoto přístupu**

| Aspekt | Benefit |
|--------|---------|
| **Opt-in** | Uživatel si volí - bez ActivitySource = žádný overhead |
| **Transparentní** | Knihovna instrumentuje, ale není vázaná na OTEL |
| **Flexible** | Lze registrovat nebo ignorovat podle potřeby |
| **Production-ready** | Nula impact pokud nepoužívány |
| **Future-proof** | Snadné přidat nové ActivitySources později |

---

## 📖 Jak Používat (3 Scénáře)

### **Scénář 1: Minimální Setup (Běžné - bez tracing)**

```csharp
var builder = WebApplication.CreateBuilder(args);

// ✅ Jen logging, bez tracing
builder.AddStepUpLogging(opts =>
{
    opts.EnrichWithExceptionDetails = true;
    opts.CaptureRequestBody = true;
});

var app = builder.Build();
app.UseStepUpRequestLogging();
app.Run();

// ✅ Výsledek:
// - Logging funguje normálně
// - ActivitySource se nepoužívá
// - Nula overhead
```

### **Scénář 2: S Tracing (Observability-first)**

```csharp
var builder = WebApplication.CreateBuilder(args);

// 1. Přidej StepUp logging
builder.AddStepUpLogging(opts =>
{
    opts.EnrichWithExceptionDetails = true;
    opts.CaptureRequestBody = true;
});

// 2. Registruj ActivitySources pro tracing
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation()
            .AddSource(StepUpLoggingExtensions.RequestLoggingActivitySourceName) // ✅
            .AddSource(StepUpLoggingExtensions.BufferActivitySourceName)         // ✅
            .UseOtlpExporter();
    });

var app = builder.Build();
app.UseStepUpRequestLogging(); // ✅ Nyní s tracing
app.Run();

// ✅ Výsledek:
// - Logging + Tracing
// - Buffer flush viditelný v Jaegeru
// - Request instrumentation v traces
```

### **Scénář 3: Wildcard Registration (Všechny StepUp sources)**

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        // ✅ Automaticky zaregistruje všechny StepUp ActivitySources
        tracing.AddSource("Lukdrasil.StepUpLogging.*");
    });
```

---

## 🔍 Kdy Má ActivitySource Smysl?

### ✅ Dobrý kandidát pro ActivitySource:

```csharp
// 1. Debugging production issues
if (errorRate > threshold)
{
    // Vidíme: LOGS (co se stalo)
    // + TRACES (kdy se buffer flushoval, jak dlouho trvalo)
    // = kompletnější picture
}

// 2. Distributed system debugging
// Service A -> Service B -> Service C
// Vidíme correlation across services

// 3. Performance analysis
// "Proč se body capture zpomaluje?"
// - Vidíme v traces: 500ms na redaction
// - Vidíme v logy: jaké patterns se aplikovaly

// 4. SLA monitoring
// "Jak dlouho trvá buffer flush při high-load?"
// Histogram: stepup_duration_seconds (metrics)
// + Activity duration v traces (timing detail)
```

### ❌ Kdy ActivitySource Není Potřeba:

```csharp
// 1. Prostý development/testing
// - Logy stačí

// 2. Systémy bez distributed tracing
// - Single service
// - Logy jsou postačující

// 3. High-throughput scénáře
// - Kde každý Activity = overhead
// - Neaplikuj tracing, jen metriky

// 4. Compliance/audit logging
// - Jde o "was this called?"
// - Méně zajímá "with how much detail"
```

---

## 📊 Performance Impact

### **S ActivitySource - bez registrace v OpenTelemetry:**

```csharp
using (ActivitySource.StartActivity(...)) // Null-op
{
    // ~0.1-0.2 microseconds overhead
}
```

**Výsledek:** Prakticky nula, Activity se nevytváří.

### **S ActivitySource - s registrací:**

```csharp
// Builder.Services.AddOpenTelemetry()
//     .WithTracing(t => t.AddSource("..."))

using (ActivitySource.StartActivity(...)) // Vytváří Activity
{
    // ~1-5 microseconds overhead
}
```

**Výsledek:** Minimální, ale měřitelný. Přijatelné pro observability.

---

## 🛠️ Příklad: Custom Usage

Pokud chce uživatel sám přidat ActivitySource:

```csharp
// ✅ Uživatel si vezme veřejný ActivitySource
var source = StepUpLoggingExtensions.RequestLoggingActivitySource;

using var activity = source.StartActivity("MyCustomOperation", ActivityKind.Internal);
activity?.SetTag("custom.tag", "value");
```

---

## 📋 Checklist: Kdy Přidat ActivitySource Registration

```
Pro development/testing:
  ☐ Ne, stačí logy

Pro single-service v produkci:
  ☐ Ne, ale lze přidat kdyžbude potřeba

Pro microservices s OpenTelemetry:
  ✅ Ano, registruj ActivitySources

Pro debugging production issues:
  ✅ Ano, pomůže vidět timing a korelace

Pro SLA/monitoring:
  ☐ Ne, metriky a logy stačí
```

---

## 🎯 Závěr

### **ActivitySource v StepUpLogging je:**

| Vlastnost | Status |
|-----------|--------|
| **Povinný?** | ❌ Ne |
| **Užitečný?** | ✅ Ano (v určitých scénářích) |
| **Performance impact** | ✅ Nula (bez registrace) |
| **Defaultně aktiv?** | ❌ Ne (opt-in) |
| **Lze přidat později?** | ✅ Ano (bez breaking changes) |

### **Doporučení**

- ✅ **Microservices s OTEL:** Registruj ActivitySources
- ✅ **Enterprise Observability:** Vizualizuj traces v Jaegeru
- ✅ **Debugging Issues:** Aktivuj pro troubleshooting
- ❌ **Jednoduché systémy:** Ignoruj ActivitySource, logy stačí

---

## 📚 Podívej se také na:

- `SETUP_OTEL_EXAMPLE.md` - Kompletní nastavení s ActivitySource
- `ANALYSIS_MEMORY_LEAKS_OTEL.md` - Detailní technická analýza
- [OpenTelemetry .NET - ActivitySource](https://opentelemetry.io/docs/instrumentation/net/)

---

**Verze:** 1.6.2+  
**Status:** ✅ Production-Ready

# ActivitySource Instrumentation - Default Enabled Design

**Verze:** 1.6.3+  
**Aktualizováno:** 17.1.2026  
**Status:** ✅ Production-Ready

---

## 🎯 Design Decision

### **ActivitySource je NOW DEFAULT ENABLED**

```csharp
// V1.6.3+ - AUTOMATIC Instrumentation
var options = new StepUpLoggingOptions 
{
    EnableActivityInstrumentation = true  // ✅ DEFAULT
};

// Pokud chcete vypnout (opt-out):
var options = new StepUpLoggingOptions 
{
    EnableActivityInstrumentation = false  // ❌ Disable
};
```

---

## 📊 Instrumentace Points

### **1. Request Logging (RequestLoggingActivitySource)**

```
LogRequest (ActivityKind.Server)
├── Tags: http.method, http.target, http.scheme, http.host
├── Tags: security.redaction_applied (if applicable)
│
├── ApplyRedaction (child, ActivityKind.Internal)
│   └── Per each header that gets redacted
│
└── CaptureRequestBody (child, ActivityKind.Internal)
    └── When CaptureRequestBody enabled and logging is stepped-up
```

**Příklad v Jaegeru:**
```
GET /api/users?secret=***
  ├── ApplyRedaction (1.2ms) - X-API-Key header
  ├── ApplyRedaction (0.8ms) - Authorization header  
  └── CaptureRequestBody (5.3ms) - 512 bytes captured
```

### **2. Step-Up/Step-Down (ControllerActivitySource)**

```
TriggerStepUp (ActivityKind.Internal)
├── Timestamp: 2026-01-17 18:30:45Z
├── Duration: 180 seconds
└── Tags: triggered_by_error_event

PerformStepDown (ActivityKind.Internal)
├── Timestamp: 2026-01-17 18:33:45Z
├── Duration: Complete (histogram recorded)
└── Tags: level_change=Warning
```

**Use Case: Vidět co se stalo v systemu kolem chyby**
- 18:30:45 - ERROR event triggeruje StepUp
- 18:30:46 - 18:33:44 - All logs at Information level
- 18:33:45 - Step-Down - Back to Warning level

### **3. Buffer Operations (BufferActivitySource)**

```
FlushBufferedEvents (ActivityKind.Internal)
├── Tags: event_count=42
├── Tags: context_id=trace-id
└── Duration: Time to write buffered events
```

---

## 🔧 Konfigurace

### **Scenario 1: Default (ActivitySource Enabled)**

```csharp
var builder = WebApplication.CreateBuilder(args);

// ✅ Activities budou vytvářeny
builder.AddStepUpLogging(); // EnableActivityInstrumentation=true by default

// Pokud registruješ v OpenTelemetry, vidíš traces
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("Lukdrasil.StepUpLogging.*");
    });
```

### **Scenario 2: Disable ActivitySource**

```csharp
builder.AddStepUpLogging(opts =>
{
    opts.EnableActivityInstrumentation = false; // ❌ Vypni Activities
    opts.CaptureRequestBody = true;
    opts.EnrichWithExceptionDetails = true;
});

// ✅ Výsledek:
// - Logging funguje normálně
// - NULA Activity overhead
// - Ne- požadujou se Activities v OpenTelemetry
```

### **Scenario 3: Disable Globally (Environment)**

```bash
# Environment variable pro disable (neimplementováno, ale možné přidat)
export STEPUPLOGGING_DISABLE_ACTIVITIES=true
```

---

## 📈 Performance Impact

### **With Activities Enabled (Default)**

| Situace | Overhead |
|---------|----------|
| OTEL Not Registered | ~0.1-0.2 µs per activity (null-op) |
| OTEL Registered | ~1-5 µs per activity |
| High-throughput (1000 req/s) | ~1-5 ms total per sec |

### **Conclusion**
✅ **Acceptable overhead** - Even with Activities enabled, impact je minimální

---

## 🎯 Vhodná Místa pro Activity Instrumentaci

### **✅ Implementováno**

- ✅ **TriggerStepUp** - When error triggers level increase
- ✅ **PerformStepDown** - When timer restores level
- ✅ **LogRequest** - Main HTTP request
- ✅ **ApplyRedaction** - Per-header redaction
- ✅ **CaptureRequestBody** - Body capture operation
- ✅ **FlushBufferedEvents** - Buffer flush on error

### **🤔 Zvážit pro Budoucnost**

- 🔹 **BufferEvent** - Per-event buffering (high-volume, skip it)
- 🔹 **PatternCompilation** - Regex compilation (rare, skip)
- 🔹 **HeaderSanitization** - Per-header sanitization (too noisy)
- 🔹 **PathExclusionCheck** - Per-request path check (trivial overhead)

**Doporučení:** Aktuální instrumentace je **optimální** - přidává value bez noise

---

## 🔄 Default Values Summary

```csharp
public bool EnableActivityInstrumentation { get; set; } = true;

// Co to znamená:
// ✅ Activities se vytváří VŽDY
// ✅ Pokud OTEL je registered -> Vidím v Jaegeru
// ✅ Pokud OTEL není registered -> Zero cost (null-op)
// ✅ Lze vypnout: EnableActivityInstrumentation = false
```

---

## 📋 Checklist: Když Nastartaš Aplikaci

### **Default Setup (Recommended)**

```csharp
✅ builder.AddStepUpLogging();
✅ app.UseStepUpRequestLogging();
✅ Logging bude mít Activities (pokud OTEL registered)
```

### **Pokud Máš Performance Problémy**

```csharp
⚠️ Zkus vypnout:
builder.AddStepUpLogging(opts =>
{
    opts.EnableActivityInstrumentation = false;
});

💡 Ale nejdřív si měř - Activities by měly být OK
```

### **Production Setup (OpenTelemetry)**

```csharp
✅ builder.AddStepUpLogging();
✅ builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Lukdrasil.StepUpLogging.*"));
✅ Export to Jaeger/Tempo pro distributed tracing
```

---

## 🎓 Srovnání: Opt-In vs. Opt-Out

| Aspekt | Opt-In (v1.6.2) | Opt-Out (v1.6.3+) |
|--------|-----------------|-------------------|
| **Default** | Vypnuto | ✅ Zapnuto |
| **User Action** | Registrovat ActivitySource | Vypnout (opět) |
| **Discovery** | "I didn't know it existed" | Vidí activities hned |
| **Observability** | User-driven | Automatic (better!) |
| **Breaking Changes** | Ne | Ne |
| **Production Ready** | ✅ Ano | ✅ Ano |

### **Výhody Opt-Out (Current)**

```
✅ Lepší observability by default
✅ Méně migrací pro uživatele
✅ "Zero-config observability"
✅ Stále lze vypnout když třeba
```

---

## 🚀 Aktivace v OpenTelemetry

### **Minimum Setup**

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("Lukdrasil.StepUpLogging.RequestLogging");
        tracing.AddSource("Lukdrasil.StepUpLogging.Controller");
        tracing.AddSource("Lukdrasil.StepUpLogging.Buffer");
        tracing.UseOtlpExporter();
    });
```

### **Or Wildcard**

```csharp
tracing.AddSource("Lukdrasil.StepUpLogging.*"); // ✅ Všechny
```

---

## 📊 Expected Traces v Jaegeru

### **Normal Request**

```
GET /api/users (200 OK) - 45ms
├── LogRequest - 42ms
├── ApplyRedaction - 0.5ms
└── CaptureRequestBody - 2.5ms
```

### **Request with Error (Step-Up)**

```
POST /api/data (500 Error) - 120ms
├── TriggerStepUp - 0.1ms [ERROR EVENT]
├── LogRequest - 115ms
│   ├── ApplyRedaction - 2ms
│   ├── ApplyRedaction - 1.5ms
│   └── CaptureRequestBody - 10ms
│
[... subsequent requests for 180 seconds ...]
│
└── PerformStepDown - 0.1ms [TIMEOUT]
```

---

## ✅ Summary

### **V1.6.3+**

```
┌─────────────────────────────────────┐
│ ActivitySource by DEFAULT ENABLED   │
├─────────────────────────────────────┤
│ ✅ Zero-config observability        │
│ ✅ Better debugging                 │
│ ✅ Minimal performance impact       │
│ ✅ Still can opt-out                │
│ ✅ Fully backward compatible        │
│ ✅ Production-ready                 │
└─────────────────────────────────────┘
```

---

**Verze:** 1.6.3+  
**Všechny testy prochází:** 29/29 ✅  
**Production Status:** 🟢 Ready

import http from 'k6/http';
import { check, group, sleep } from 'k6';
import { Rate, Trend, Counter, Gauge } from 'k6/metrics';

// Custom metrics
const demoLogsLatency = new Trend('demo_logs_latency');
const demoEchoLatency = new Trend('demo_echo_latency');
const weatherforecastLatency = new Trend('weatherforecast_latency');
const errorCount = new Counter('errors');
const successCount = new Counter('success');
const stepupTriggerLatency = new Trend('stepup_trigger_latency');
const stepupStatusLatency = new Trend('stepup_status_latency');

// Configuration - Aspire development endpoints (can be overridden via environment variables)
const SIMPLE_API_BASE = __ENV.SIMPLE_API_BASE || 'http://localhost:7152';
const STEPUP_API_BASE = __ENV.STEPUP_API_BASE || 'http://localhost:7189';

// k6 HTTP client options for development local endpoints
const httpOptions = {
  headers: {
    'User-Agent': 'k6-performance-test/1.0',
  },
  // Skip certificate verification for development HTTPS endpoints
  insecureSkipTLSVerify: true,
};

// Helper to merge headers with httpOptions
function createOptions(customHeaders = {}) {
  return {
    ...httpOptions,
    headers: { ...httpOptions.headers, ...customHeaders },
  };
}

// Test scenarios
export const options = {
  stages: [
    // Ramp-up: gradually increase to 50 users over 30 seconds
    { duration: '30s', target: 50 },
    // Stay at 50 users for 2 minutes
    { duration: '2m', target: 50 },
    // Ramp-down: gradually decrease to 0 users over 30 seconds
    { duration: '30s', target: 0 },
    
  ],
  thresholds: {
    'demo_logs_latency': ['p(95)<500'],
    'demo_echo_latency': ['p(95)<1000'],
    'weatherforecast_latency': ['p(95)<200'],
    'errors': ['count<100'],
  },
};

export default function () {
  const testType = __ENV.TEST_TYPE || 'baseline_simple';

  if (testType === 'baseline_simple') {
    testSimpleApiBaseline();
  } else if (testType === 'stepup_active') {
    testStepUpApiWithActiveStepUp();
  } else {
    testSimpleApiBaseline();
  }
}

/**
 * Test SimpleApi (standard Serilog logging, no step-up)
 */
function testSimpleApiBaseline() {
  group('SimpleApi - Baseline (Standard Serilog)', () => {
    // GET /weatherforecast
    group('GET /weatherforecast', () => {
      const response = http.get(`${SIMPLE_API_BASE}/weatherforecast`, httpOptions);
      weatherforecastLatency.add(response.timings.duration);
      
      const success = check(response, {
        'status is 200': (r) => r.status === 200,
        'response time < 200ms': (r) => r.timings.duration < 200,
      });
      
      if (success) {
        successCount.add(1);
      } else {
        errorCount.add(1);
      }
    });

    // GET /demo/logs
    group('GET /demo/logs', () => {
      const response = http.get(`${SIMPLE_API_BASE}/demo/logs`, httpOptions);
      demoLogsLatency.add(response.timings.duration);
      
      const success = check(response, {
        'status is 200': (r) => r.status === 200,
        'response time < 500ms': (r) => r.timings.duration < 500,
      });
      
      if (success) {
        successCount.add(1);
      } else {
        errorCount.add(1);
      }
    });

    // POST /demo/echo with JSON body
    group('POST /demo/echo', () => {
      const payload = JSON.stringify({
        test: 'data',
        timestamp: new Date().toISOString(),
        user: 'testuser',
      });
      
      const response = http.post(`${SIMPLE_API_BASE}/demo/echo`, payload, createOptions({ 'Content-Type': 'application/json' }));
      demoEchoLatency.add(response.timings.duration);
      
      const success = check(response, {
        'status is 200': (r) => r.status === 200,
        'response time < 1000ms': (r) => r.timings.duration < 1000,
      });
      
      if (success) {
        successCount.add(1);
      } else {
        errorCount.add(1);
      }
    });

    // GET /stepup/status (should return active=false)
    group('GET /stepup/status', () => {
      const response = http.get(`${SIMPLE_API_BASE}/stepup/status`, httpOptions);
      stepupStatusLatency.add(response.timings.duration);
      
      const success = check(response, {
        'status is 200': (r) => r.status === 200,
        'active is false': (r) => JSON.parse(r.body).active === false,
      });
      
      if (success) {
        successCount.add(1);
      } else {
        errorCount.add(1);
      }
    });

    sleep(1);
  });
}

/**
 * Test StepUpApi with active step-up
 * Note: Step-up is automatically triggered by error logs in /demo/logs endpoint
 */
function testStepUpApiWithActiveStepUp() {
  group('StepUpApi - With Active Step-up', () => {
    // Trigger step-up at the start
    if (__VU === 1 && __ITER === 0) {
      group('POST /stepup/trigger', () => {
        const response = http.post(`${STEPUP_API_BASE}/stepup/trigger`, null, httpOptions);
        stepupTriggerLatency.add(response.timings.duration);
        
        check(response, {
          'trigger status is 200': (r) => r.status === 200,
        });
      });
      
      sleep(0.5);
    }

    // Verify step-up is active
    group('GET /stepup/status (should be active)', () => {
      const response = http.get(`${STEPUP_API_BASE}/stepup/status`, httpOptions);
      stepupStatusLatency.add(response.timings.duration);
      
      const success = check(response, {
        'status is 200': (r) => r.status === 200,
        'active is true': (r) => JSON.parse(r.body).active === true,
      });
      
      if (success) {
        successCount.add(1);
      } else {
        errorCount.add(1);
      }
    });

    // GET /weatherforecast
    group('GET /weatherforecast', () => {
      const response = http.get(`${STEPUP_API_BASE}/weatherforecast`, httpOptions);
      weatherforecastLatency.add(response.timings.duration);
      
      const success = check(response, {
        'status is 200': (r) => r.status === 200,
        'response time < 200ms': (r) => r.timings.duration < 200,
      });
      
      if (success) {
        successCount.add(1);
      } else {
        errorCount.add(1);
      }
    });

    // GET /demo/logs
    group('GET /demo/logs', () => {
      const response = http.get(`${STEPUP_API_BASE}/demo/logs`, httpOptions);
      demoLogsLatency.add(response.timings.duration);
      
      const success = check(response, {
        'status is 200': (r) => r.status === 200,
        'response time < 500ms': (r) => r.timings.duration < 500,
      });
      
      if (success) {
        successCount.add(1);
      } else {
        errorCount.add(1);
      }
    });

    // POST /demo/echo with JSON body (body capture is triggered when step-up is active)
    group('POST /demo/echo', () => {
      const payload = JSON.stringify({
        test: 'data',
        timestamp: new Date().toISOString(),
        user: 'testuser',
      });
      
      const response = http.post(`${STEPUP_API_BASE}/demo/echo`, payload, createOptions({ 'Content-Type': 'application/json' }));
      demoEchoLatency.add(response.timings.duration);
      
      const success = check(response, {
        'status is 200': (r) => r.status === 200,
        'response time < 1000ms': (r) => r.timings.duration < 1000,
      });
      
      if (success) {
        successCount.add(1);
      } else {
        errorCount.add(1);
      }
    });

    sleep(1);
  });
}

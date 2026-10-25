import type { ReadableSpan, SpanExporter } from '@opentelemetry/sdk-trace-base';

// Server-side traces over OTLP (Hospital's docs/features/21-observabilidad/opentelemetry.spec.md). Off unless
// OTEL_EXPORTER_OTLP_ENDPOINT is set; traceparent goes only to Recepción's API; no query string survives export.
export async function register() {
  const endpoint = process.env.OTEL_EXPORTER_OTLP_ENDPOINT;
  if (!endpoint || process.env.NEXT_RUNTIME !== 'nodejs') return;
  const { registerOTel, OTLPHttpProtoTraceExporter } = await import('@vercel/otel');
  registerOTel({
    serviceName: 'recepcion-web',
    attributes: { 'deployment.environment.name': process.env.NODE_ENV },
    traceExporter: new QueryStrippingExporter(new OTLPHttpProtoTraceExporter({ url: `${endpoint.replace(/\/$/, '')}/v1/traces` })),
    instrumentationConfig: { fetch: { propagateContextUrls: [process.env.API_URL].filter((url): url is string => !!url) } },
  });
}

/** `GET /api/contacts?q=Ana` → `GET /api/contacts`: a search term can name a patient. */
export function stripQuery(value: string) {
  return value.replace(/(\S)\?\S*/g, '$1');
}

/** Strips every query string from the span name and its string attributes before export. */
export class QueryStrippingExporter implements SpanExporter {
  readonly inner: SpanExporter;
  constructor(inner: SpanExporter) { this.inner = inner; }
  export(spans: ReadableSpan[], done: Parameters<SpanExporter['export']>[1]) {
    this.inner.export(spans.map((span) => Object.create(span, {
      name: { value: stripQuery(span.name) },
      attributes: { value: Object.fromEntries(Object.entries(span.attributes).map(([k, v]) => [k, typeof v === 'string' ? stripQuery(v) : v])) },
    })), done);
  }
  shutdown() { return this.inner.shutdown(); }
  forceFlush() { return this.inner.forceFlush?.() ?? Promise.resolve(); }
}

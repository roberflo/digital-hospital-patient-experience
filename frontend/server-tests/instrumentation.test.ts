import { test } from 'node:test';
import assert from 'node:assert/strict';
import type { ReadableSpan, SpanExporter } from '@opentelemetry/sdk-trace-base';
import { QueryStrippingExporter, register, stripQuery } from '../src/instrumentation.ts';

// OBS-1 and OBS-3 (Hospital's docs/features/21-observabilidad/opentelemetry.spec.md).
test('registers nothing without a collector', async () => {
  delete process.env.OTEL_EXPORTER_OTLP_ENDPOINT;
  await register(); // would import @vercel/otel and fail outside Next if it did not return first
});
test('strips the query from URLs and span names, nothing else', () => {
  assert.equal(stripQuery('GET /api/contacts?q=Ana%20P'), 'GET /api/contacts');
  assert.equal(stripQuery('http://recepcion-api:8080/api/contacts?q=Ana&x=1'), 'http://recepcion-api:8080/api/contacts');
  assert.equal(stripQuery('/api/conversations/0f7c'), '/api/conversations/0f7c');
});
test('exports spans without the query in name or attributes', () => {
  const exported: ReadableSpan[] = [];
  const inner = { export: (spans: ReadableSpan[]) => exported.push(...spans), shutdown: async () => {} } as unknown as SpanExporter;
  const span = { name: 'GET /api/contacts?q=Ana', attributes: { 'http.target': '/api/contacts?q=Ana', 'http.status_code': 200 }, spanContext: () => ({ traceId: 't' }) } as unknown as ReadableSpan;
  new QueryStrippingExporter(inner).export([span], () => {});
  assert.equal(exported[0]!.name, 'GET /api/contacts');
  assert.deepEqual({ ...exported[0]!.attributes }, { 'http.target': '/api/contacts', 'http.status_code': 200 });
  assert.equal(exported[0]!.spanContext().traceId, 't');
});

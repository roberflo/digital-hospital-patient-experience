const destinations = new Set(['/', '/inbox', '/whatsapp', '/session-restored']);
export function safeReturnPath(value?: unknown): string {
  if (
    typeof value !== 'string' ||
    !value ||
    !value.startsWith('/') ||
    value.startsWith('//') ||
    /[\\\r\n]/.test(value)
  )
    return '/';
  try {
    const parsed = new URL(value, 'https://recepcion.invalid');
    if (parsed.origin !== 'https://recepcion.invalid' || !destinations.has(parsed.pathname))
      return '/';
    return parsed.pathname + parsed.search + parsed.hash;
  } catch {
    return '/';
  }
}
export function loginUrl(returnTo: string) {
  return '/login?callbackUrl=' + encodeURIComponent(safeReturnPath(returnTo));
}
export function pathWithQuery(path: string, params: Record<string, string | string[] | undefined>) {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (typeof value === 'string') query.set(key, value);
    else if (Array.isArray(value)) value.forEach((v) => query.append(key, v));
  }
  return path + (query.size ? '?' + query : '');
}

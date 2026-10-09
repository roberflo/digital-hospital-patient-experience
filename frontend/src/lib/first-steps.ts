// «Primeros pasos» del Administrador: el estado de cada paso sale de lecturas reales del backend.
// done/pending solo cuando la lectura respondió; loading = aún no responde; unknown = la lectura
// falló (ni hecho ni pendiente); failed = Hospital está configurado pero la comprobación falló;
// blocked = a la instalación le falta un ajuste que resuelve el operador, no el administrador.
export type StepState = 'done' | 'pending' | 'failed' | 'loading' | 'unknown' | 'blocked';
export type StepId = 'hospital' | 'whatsapp' | 'team' | 'agent';
export type Read<T> = { data?: T; error?: unknown };
export type Checked<T> = Read<T> & { validating?: boolean };
export type Installation = {
  kapsoKey: boolean;
  webhookUrl: boolean;
  webhookSecret: boolean;
  agentModel: boolean;
  autoSend: boolean;
};
export type StepChannel = {
  id: string;
  name?: string;
  phoneNumberId: string;
  enabled: boolean;
  lastWebhookAt?: string | null;
};
export type Reception = { receivesMessages: boolean; signatureMatches: boolean };
export type Step = {
  id: StepId;
  state: StepState;
  // blocked: los ajustes de instalación que faltan, por nombre.
  missing?: string[];
  // whatsapp pendiente: qué sub-paso toca.
  need?: 'link' | 'activate' | 'reception';
  channel?: StepChannel;
};

export function readState<T>(read: Read<T>, done: (data: T) => boolean): StepState {
  if (read.error) return 'unknown';
  if (read.data === undefined) return 'loading';
  return done(read.data) ? 'done' : 'pending';
}

export function hospitalState(
  connection: Read<{ configured: boolean }>,
  check: Checked<{ connected: boolean }>,
): StepState {
  const configured = readState(connection, (c) => c.configured);
  if (configured !== 'done') return configured;
  if (check.validating) return 'loading';
  // SWR conserva el último dato cuando una revalidación falla: el error manda.
  if (check.error) return 'failed';
  if (check.data === undefined) return 'loading';
  return check.data.connected ? 'done' : 'failed';
}

// El canal «demo» del sembrado sintético no es un número conectado.
const real = (rows: StepChannel[]) => rows.filter((c) => !c.phoneNumberId.startsWith('demo'));
const missing = (pairs: [boolean, string][]) => pairs.filter(([ok]) => !ok).map(([, name]) => name);

// El canal activo cuya recepción aún hay que preguntar a Kapso. null = no hace falta preguntar:
// no hay canal activo, o ya llegó un evento firmado por alguno (lastWebhookAt), que es la prueba
// más fuerte de que la recepción funciona y evita tocar Kapso en cada carga.
export function receptionProbe(rows: StepChannel[] | undefined): StepChannel | null {
  const active = real(rows ?? []).filter((c) => c.enabled);
  return active.some((c) => c.lastWebhookAt) ? null : (active[0] ?? null);
}

export function whatsappStep(
  installation: Read<Installation>,
  channels: Read<StepChannel[]>,
  reception: Checked<Reception>,
): Step {
  const id = 'whatsapp' as const;
  if (installation.error || channels.error) return { id, state: 'unknown' };
  if (!installation.data || !channels.data) return { id, state: 'loading' };
  const i = installation.data;
  const lacks = missing([
    [i.kapsoKey, 'KAPSO_API_KEY'],
    [i.webhookUrl, 'KAPSO_WEBHOOK_URL (https)'],
    [i.webhookSecret, 'KAPSO_WEBHOOK_SECRET'],
  ]);
  if (lacks.length) return { id, state: 'blocked', missing: lacks };
  const rows = real(channels.data);
  if (!rows.length) return { id, state: 'pending', need: 'link' };
  if (!rows.some((c) => c.enabled))
    return { id, state: 'pending', need: 'activate', channel: rows[0] };
  const channel = receptionProbe(rows);
  if (!channel) return { id, state: 'done', channel: rows.find((c) => c.enabled) };
  if (reception.validating) return { id, state: 'loading', channel };
  if (reception.error) return { id, state: 'unknown', channel };
  if (!reception.data) return { id, state: 'loading', channel };
  return reception.data.receivesMessages && reception.data.signatureMatches
    ? { id, state: 'done', channel }
    : { id, state: 'pending', need: 'reception', channel };
}

export function agentStep(enabled: boolean, installation: Read<Installation>): Step {
  const id = 'agent' as const;
  if (installation.error) return { id, state: 'unknown' };
  if (!installation.data) return { id, state: 'loading' };
  // Encendido sin proveedor de modelo o sin envío automático no atiende a nadie: no es «hecho».
  const lacks = missing([
    [installation.data.agentModel, 'NVIDIA_API_KEY'],
    [installation.data.autoSend, 'SEND_ENABLED=true'],
  ]);
  if (lacks.length) return { id, state: 'blocked', missing: lacks };
  return { id, state: enabled ? 'done' : 'pending' };
}

export type FirstStepsInput = {
  me: { role: string; subject: string; tenant: { agentEnabled: boolean } };
  connection: Read<{ configured: boolean }>;
  check: Checked<{ connected: boolean }>;
  installation: Read<Installation>;
  channels: Read<StepChannel[]>;
  reception: Checked<Reception>;
  workload: Read<{ members: { subject: string; disabled: boolean }[] }>;
};

// null = sin guía: otro rol, todo hecho, o todavía no hay ningún paso que mostrar como no hecho.
export function firstSteps(input: FirstStepsInput): Step[] | null {
  const { me } = input;
  if (me.role !== 'admin') return null;
  const steps: Step[] = [
    { id: 'hospital', state: hospitalState(input.connection, input.check) },
    whatsappStep(input.installation, input.channels, input.reception),
    {
      id: 'team',
      state: readState(input.workload, (w) =>
        w.members.some((m) => !m.disabled && m.subject !== me.subject),
      ),
    },
    agentStep(me.tenant.agentEnabled, input.installation),
  ];
  // Mientras todo esté hecho o cargando no se pinta nada: quien ya terminó no ve un parpadeo.
  return steps.some((s) => s.state !== 'done' && s.state !== 'loading') ? steps : null;
}

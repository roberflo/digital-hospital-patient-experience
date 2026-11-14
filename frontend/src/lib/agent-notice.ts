import type { SendBlock } from './api';

/**
 * What the conversation says about the agent, one rule for the notice and the header badge:
 * «attending» only if the server would let the agent answer now (INV-4 of docs/agent-trail.md).
 */
export function agentNotice({
  status,
  agentEnabled,
  agentBlock,
  role,
}: {
  status: string;
  agentEnabled: boolean;
  agentBlock: SendBlock | null;
  role: string;
}) {
  const off = !agentEnabled && status !== 'closed';
  const blocked = off ? null : agentBlock;
  const text = blocked
    ? `El agente no podría responder: ${blocked.reason}`
    : off
      ? role === 'admin'
        ? 'El agente está apagado para todo el hospital. Actívalo en Configuración.'
        : 'El agente está apagado para todo el hospital. Pide al administrador que lo active.'
      : status === 'agent'
        ? 'El agente está atendiendo esta conversación.'
        : status === 'closed'
          ? 'La conversación está cerrada.'
          : 'Tu equipo está a cargo. El agente está en pausa.';
  return {
    text,
    /** The value for `StatusBadge`: an «agent» conversation nobody can answer is unattended (MR-46). */
    badge: status === 'agent' && (off || blocked) ? 'unattended' : status,
    /** «Activar agente» is pointless while the agent is off or blocked; «Tomar conversación» never is. */
    canActivate: status === 'agent' || !(off || blocked),
  };
}

'use client';
import { useState } from 'react';
import useSWR from 'swr';
import { Check, Info, Loader2, RotateCcw } from 'lucide-react';
import { toast } from 'sonner';
import { api, fetcher } from '@/lib/api';
import { Alert, AlertDescription } from './ui/alert';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from './ui/card';
import { Checkbox } from './ui/checkbox';
import {
  Field,
  FieldContent,
  FieldDescription,
  FieldGroup,
  FieldLabel,
  FieldLegend,
  FieldSet,
  FieldTitle,
} from './ui/field';
import { RadioGroup, RadioGroupItem } from './ui/radio-group';

// «Cómo atiende mi recepción». The hospital decides among closed options and ours is marked as
// recommended. What its setup cannot do yet is shown switched off with the reason, never as a
// choice that would change nothing.
type Attention = {
  symptoms: 'ask' | 'person';
  noSlotSoon: 'ask' | 'nearest';
  doctorChat: boolean;
  booking: 'doctor' | 'hours';
  unregistered: 'register' | 'person';
  menu: string[] | null;
};
type AttentionData = {
  settings: Attention;
  recommended: Attention;
  menuOptions: string[];
  context: {
    emergencyPhone: boolean;
    emergencyWhatsApp: boolean;
    doctors: number;
    doctorsWithWhatsApp: number;
  };
};
type Option = { value: string; title: string; detail: string };
type Decision = {
  key: 'symptoms' | 'noSlotSoon' | 'doctorChat' | 'booking' | 'unregistered';
  question: string;
  options: Option[];
};

const decisions: Decision[] = [
  {
    key: 'symptoms',
    question: 'Cuando un paciente cuenta sus síntomas',
    options: [
      {
        value: 'ask',
        title: 'Atenderlo y preguntar si es una emergencia',
        detail: 'Le ofrece una cita y un botón de Emergencia. El paciente decide.',
      },
      {
        value: 'person',
        title: 'Ofrecerle pasar con una persona',
        detail: 'El agente no sigue con la cita: le pregunta si quiere hablar con tu equipo.',
      },
    ],
  },
  {
    key: 'noSlotSoon',
    question: 'Cuando no hay citas en las próximas 8 horas',
    options: [
      {
        value: 'ask',
        title: 'Preguntar si es una emergencia',
        detail: 'Además de mostrar el siguiente horario libre.',
      },
      {
        value: 'nearest',
        title: 'Solo ofrecer la cita más cercana',
        detail: 'No pregunta por emergencias al agendar.',
      },
    ],
  },
  {
    key: 'doctorChat',
    question: 'Cuando un paciente pide hablar con una persona o con el doctor',
    options: [
      {
        value: 'true',
        title: 'Pasarlo a tu equipo y ofrecer el WhatsApp del doctor',
        detail: 'Solo de los doctores que tienen WhatsApp para pacientes en Equipo.',
      },
      {
        value: 'false',
        title: 'Solo pasarlo a tu equipo',
        detail: 'Tu equipo responde en la misma conversación.',
      },
    ],
  },
  {
    key: 'booking',
    question: 'Al agendar una cita, qué se pregunta primero',
    options: [
      {
        value: 'doctor',
        title: 'El doctor y después el horario',
        detail: 'Si ese día solo hay un doctor con horario libre, no se pregunta.',
      },
      {
        value: 'hours',
        title: 'Directo los horarios',
        detail: 'Muestra las horas libres de todos los doctores.',
      },
    ],
  },
  {
    key: 'unregistered',
    question: 'Cuando escribe alguien que aún no es paciente',
    options: [
      {
        value: 'register',
        title: 'Registrarlo por WhatsApp',
        detail: 'El agente le pide sus datos uno por uno y agenda su primera cita.',
      },
      {
        value: 'person',
        title: 'Pasarlo a recepción',
        detail: 'Tu equipo lo registra y le agenda.',
      },
    ],
  },
];
const menuLabels: Record<string, string> = {
  AGENDAR: 'Agendar cita',
  MISCITAS: 'Mis citas',
  RECETA: 'Mi receta',
  PERSONA: 'Hablar con persona',
};
const same = (a: Attention, b: Attention, all: string[]) =>
  decisions.every((d) => a[d.key] === b[d.key]) &&
  all.every((option) => (a.menu ?? all).includes(option) === (b.menu ?? all).includes(option));

export function AttentionSettings() {
  const { data, error, mutate } = useSWR<AttentionData>('/settings/attention', fetcher);
  if (error)
    return (
      <Alert variant="destructive">
        <AlertDescription>{error.message}</AlertDescription>
      </Alert>
    );
  if (!data) return null;
  // Remounted with what was saved: the draft below is only what has not been saved yet.
  return <AttentionForm key={JSON.stringify(data.settings)} data={data} onSaved={() => mutate()} />;
}

function AttentionForm({ data, onSaved }: { data: AttentionData; onSaved: () => void }) {
  const { recommended, menuOptions, context } = data;
  const [draft, setDraft] = useState<Attention>(data.settings);
  const [saving, setSaving] = useState(false);
  const shown = draft.menu ?? menuOptions;
  // Why a decision cannot be made yet, in the hospital's own terms.
  const blocked: Partial<Record<Decision['key'], string>> = {
    ...(context.emergencyPhone
      ? {}
      : {
          symptoms:
            'Sin teléfono de urgencias el agente no pregunta por emergencias: ofrece la cita más cercana. Agrégalo arriba, en «Teléfono de urgencias».',
          noSlotSoon:
            'Sin teléfono de urgencias el agente solo ofrece la cita más cercana. Agrégalo arriba, en «Teléfono de urgencias».',
        }),
    ...(context.doctorsWithWhatsApp > 0
      ? {}
      : {
          doctorChat:
            'Ningún doctor tiene WhatsApp para pacientes, así que hoy solo se pasa a tu equipo. Agrégalo en Equipo.',
        }),
  };
  async function save() {
    setSaving(true);
    try {
      await api('/settings/attention', 'PUT', draft);
      toast.success('Así atenderá tu recepción');
      onSaved();
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setSaving(false);
    }
  }
  return (
    <Card className="min-w-0">
      <CardHeader>
        <CardTitle>
          <h2>Cómo atiende mi recepción</h2>
        </CardTitle>
        <CardDescription>
          Tú decides cómo responde el agente. La opción marcada como recomendada es la que
          sugerimos; puedes cambiarla cuando quieras.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <FieldGroup>
          {decisions.map((decision) => {
            const value = String(draft[decision.key]);
            const reason = blocked[decision.key];
            return (
              <FieldSet key={decision.key} disabled={!!reason}>
                <FieldLegend variant="label">{decision.question}</FieldLegend>
                {reason && (
                  <FieldDescription>
                    <Info className="mr-1 inline size-3.5 align-text-bottom" />
                    {reason}
                  </FieldDescription>
                )}
                <RadioGroup
                  value={value}
                  disabled={!!reason}
                  onValueChange={(next) =>
                    setDraft({
                      ...draft,
                      [decision.key]: decision.key === 'doctorChat' ? next === 'true' : next,
                    })
                  }
                >
                  {decision.options.map((option) => (
                    <FieldLabel
                      key={option.value}
                      htmlFor={`attention-${decision.key}-${option.value}`}
                    >
                      <Field orientation="horizontal">
                        <FieldContent>
                          <FieldTitle>
                            {option.title}
                            {String(recommended[decision.key]) === option.value && (
                              <Badge variant="secondary">Recomendada</Badge>
                            )}
                          </FieldTitle>
                          <FieldDescription>{option.detail}</FieldDescription>
                        </FieldContent>
                        <RadioGroupItem
                          value={option.value}
                          id={`attention-${decision.key}-${option.value}`}
                        />
                      </Field>
                    </FieldLabel>
                  ))}
                </RadioGroup>
              </FieldSet>
            );
          })}
          <FieldSet>
            <FieldLegend variant="label">Qué opciones muestra el menú de bienvenida</FieldLegend>
            <FieldDescription>
              Recomendamos las cuatro. Debe quedar al menos una; lo que quites se sigue atendiendo
              si el paciente lo pide con sus palabras.
            </FieldDescription>
            <div className="grid gap-3 sm:grid-cols-2">
              {menuOptions.map((option) => (
                <Field key={option} orientation="horizontal">
                  <Checkbox
                    id={`attention-menu-${option}`}
                    checked={shown.includes(option)}
                    disabled={shown.length === 1 && shown.includes(option)}
                    onCheckedChange={(checked) => {
                      const next = menuOptions.filter((o) =>
                        o === option ? checked === true : shown.includes(o),
                      );
                      setDraft({
                        ...draft,
                        menu: next.length === menuOptions.length ? null : next,
                      });
                    }}
                  />
                  <FieldLabel htmlFor={`attention-menu-${option}`} className="font-normal">
                    {menuLabels[option] ?? option}
                  </FieldLabel>
                </Field>
              ))}
            </div>
          </FieldSet>
        </FieldGroup>
        <p className="mt-6 text-sm text-muted-foreground">
          Esto no se puede cambiar: el agente nunca diagnostica ni recomienda dosis, y quien escribe
          «emergencia» pasa de inmediato con una persona.
        </p>
      </CardContent>
      <CardFooter className="flex flex-wrap justify-end gap-2">
        <Button
          type="button"
          variant="outline"
          disabled={saving || same(draft, recommended, menuOptions)}
          onClick={() => setDraft(recommended)}
        >
          <RotateCcw />
          Volver a lo recomendado
        </Button>
        <Button
          type="button"
          disabled={saving || same(draft, data.settings, menuOptions)}
          onClick={save}
        >
          {saving ? <Loader2 className="animate-spin" /> : <Check />}
          Guardar cambios
        </Button>
      </CardFooter>
    </Card>
  );
}

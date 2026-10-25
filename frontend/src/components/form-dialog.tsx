'use client';
import { useEffect, useState, useRef } from 'react';
import useSWR from 'swr';
import { Search, Check } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from './ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from './ui/dialog';
import { Field as UiField, FieldDescription, FieldError, FieldGroup, FieldLabel } from './ui/field';
import { Input } from './ui/input';
import { InputGroup, InputGroupAddon, InputGroupInput } from './ui/input-group';
import { NativeSelect, NativeSelectOption } from './ui/native-select';
import { Spinner } from './ui/spinner';
import { Textarea } from './ui/textarea';
import { fetcher, type Contact } from '@/lib/api';
import { OTHER_COUNTRY, phoneCountries, phoneToSend, splitPhone } from '@/lib/phone';
import { ErrorBox } from './workspace-shared';
type Field = {
  name: string;
  label: string;
  type?: string;
  required?: boolean;
  options?: { value: string; label: string }[];
  value?: string;
  placeholder?: string;
  description?: string;
};
export function FormDialog({
  title,
  description,
  fields,
  open,
  onClose,
  onSubmit,
}: {
  title: string;
  description?: string;
  fields: Field[];
  open: boolean;
  onClose: () => void;
  onSubmit: (values: Record<string, string>) => Promise<void>;
}) {
  const [busy, setBusy] = useState(false);
  return (
    <Dialog
      open={open}
      onOpenChange={(v) => {
        if (!v) onClose();
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>
            {description ?? 'Completa los datos para continuar.'}
          </DialogDescription>
        </DialogHeader>
        <form
          onSubmit={async (e) => {
            e.preventDefault();
            setBusy(true);
            try {
              await onSubmit(
                Object.fromEntries(new FormData(e.currentTarget)) as Record<string, string>,
              );
              onClose();
              toast.success('Cambios guardados');
            } catch (err) {
              toast.error((err as Error).message);
            } finally {
              setBusy(false);
            }
          }}
        >
          <FieldGroup>
            {fields.map((f) => (
              <UiField key={f.name}>
                <FieldLabel htmlFor={'field-' + f.name}>{f.label}</FieldLabel>
                {f.type === 'phone' ? (
                  <PhoneField
                    id={'field-' + f.name}
                    name={f.name}
                    stored={f.value}
                    required={f.required}
                  />
                ) : f.type === 'contact' ? (
                  <ContactPicker
                    id={'field-' + f.name}
                    name={f.name}
                    label={f.label}
                    required={f.required}
                  />
                ) : f.options ? (
                  <NativeSelect
                    id={'field-' + f.name}
                    className="w-full"
                    name={f.name}
                    defaultValue={f.value}
                    required={f.required}
                  >
                    <NativeSelectOption value="">Selecciona una opción</NativeSelectOption>
                    {f.options.map((o) => (
                      <NativeSelectOption key={o.value} value={o.value}>
                        {o.label}
                      </NativeSelectOption>
                    ))}
                  </NativeSelect>
                ) : f.type === 'textarea' ? (
                  <Textarea
                    id={'field-' + f.name}
                    rows={4}
                    name={f.name}
                    defaultValue={f.value}
                    required={f.required}
                    maxLength={10000}
                  />
                ) : (
                  <Input
                    id={'field-' + f.name}
                    name={f.name}
                    type={f.type ?? 'text'}
                    inputMode={f.type === 'tel' ? 'tel' : undefined}
                    defaultValue={f.value}
                    required={f.required}
                    placeholder={f.placeholder}
                    maxLength={f.type === 'text' || !f.type ? 500 : undefined}
                  />
                )}
                {f.description && <FieldDescription>{f.description}</FieldDescription>}
              </UiField>
            ))}
            <DialogFooter>
              <Button type="button" variant="outline" onClick={onClose}>
                Cancelar
              </Button>
              <Button disabled={busy}>{busy ? <Spinner /> : <Check />}Guardar</Button>
            </DialogFooter>
          </FieldGroup>
        </form>
      </DialogContent>
    </Dialog>
  );
}
/** Criterio 29: country (El Salvador +503 by default) and local number; the form sends E.164, or the stored value when untouched. */
function PhoneField({
  id,
  name,
  stored,
  required,
}: {
  id: string;
  name: string;
  stored?: string;
  required?: boolean;
}) {
  const start = splitPhone(stored);
  const [country, setCountry] = useState(start.country);
  const [local, setLocal] = useState(start.local);
  const number = useRef<HTMLInputElement>(null);
  const result = phoneToSend(country, local, stored);
  const error = local.trim() && 'error' in result ? result.error : '';
  // Said once the person leaves the number or tries to save, not on every keystroke.
  const [touched, setTouched] = useState(false);
  const shown = touched ? error : '';
  useEffect(() => number.current?.setCustomValidity(error), [error]);
  return (
    <div className="flex flex-col gap-2">
      <div className="flex gap-2">
        <NativeSelect
          aria-label="País del teléfono"
          className="w-40 shrink-0"
          value={country}
          onChange={(e) => setCountry(e.target.value)}
        >
          {phoneCountries.map((c) => (
            <NativeSelectOption key={c.code} value={c.code}>
              {c.name} +{c.code}
            </NativeSelectOption>
          ))}
          <NativeSelectOption value={OTHER_COUNTRY}>Otro (+ código)</NativeSelectOption>
        </NativeSelect>
        <Input
          ref={number}
          id={id}
          type="tel"
          inputMode="tel"
          autoComplete="tel"
          required={required}
          value={local}
          onChange={(e) => setLocal(e.target.value)}
          onBlur={() => setTouched(true)}
          onInvalid={() => setTouched(true)}
          placeholder={
            country === OTHER_COUNTRY
              ? '+44 7700 900123'
              : country === '1'
                ? '415 555 0100'
                : '7000 0000'
          }
          aria-invalid={shown ? true : undefined}
          aria-describedby={shown ? id + '-error' : undefined}
        />
        <input type="hidden" name={name} value={'phone' in result ? result.phone : ''} />
      </div>
      {shown && (
        <FieldError id={id + '-error'} role={undefined}>
          {shown}
        </FieldError>
      )}
    </div>
  );
}
/** Finds a contact on the server, not in the first page of 100 (MR-17). With `patientId` it
 * resolves the one contact linked to that Hospital patient. */
export function ContactPicker({
  id,
  name = 'contactId',
  label,
  patientId,
  linkedOnly = false,
  empty = 'Selecciona un contacto',
  required = false,
  onChange,
}: {
  id: string;
  name?: string;
  label: string;
  patientId?: string;
  linkedOnly?: boolean;
  empty?: string;
  required?: boolean;
  onChange?: (contactId: string) => void;
}) {
  const [query, setQuery] = useState('');
  const [term, setTerm] = useState('');
  useEffect(() => {
    const wait = setTimeout(() => setTerm(query.trim()), 250);
    return () => clearTimeout(wait);
  }, [query]);
  const [picked, setPicked] = useState<Contact>();
  const { data, error } = useSWR<Contact[]>(
    patientId
      ? '/contacts?patientId=' + encodeURIComponent(patientId)
      : '/contacts?q=' + encodeURIComponent(term) + (linkedOnly ? '&linked=true' : ''),
    fetcher,
  );
  const found = data ?? [];
  // The chosen contact stays choosable while the search moves on.
  const options = picked && !found.some((c) => c.id === picked.id) ? [picked, ...found] : found;
  const value = picked?.id ?? (patientId ? (found[0]?.id ?? '') : '');
  return (
    <div className="flex flex-col gap-2">
      {!patientId && (
        <InputGroup>
          <InputGroupInput
            type="search"
            aria-label={'Buscar ' + label.toLocaleLowerCase('es')}
            placeholder="Busca por nombre o teléfono…"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            maxLength={100}
          />
          <InputGroupAddon>
            <Search />
          </InputGroupAddon>
        </InputGroup>
      )}
      <NativeSelect
        id={id}
        name={name}
        className="w-full"
        required={required}
        value={value}
        onChange={(e) => {
          setPicked(options.find((c) => c.id === e.target.value));
          onChange?.(e.target.value);
        }}
      >
        <NativeSelectOption value="">{data ? empty : 'Buscando…'}</NativeSelectOption>
        {options.map((c) => (
          <NativeSelectOption value={c.id} key={c.id}>
            {c.name}
          </NativeSelectOption>
        ))}
      </NativeSelect>
      <ErrorBox error={error} />
      {data?.length === 100 && (
        <FieldDescription>
          Hay más resultados: escribe más del nombre o del teléfono.
        </FieldDescription>
      )}
      {data && !found.length && (
        <FieldDescription>
          {patientId
            ? 'Este paciente no tiene un contacto vinculado en Recepción.'
            : term
              ? `Nadie coincide con «${term}».`
              : linkedOnly
                ? 'Aún no hay pacientes vinculados.'
                : 'Aún no hay contactos.'}
        </FieldDescription>
      )}
    </div>
  );
}

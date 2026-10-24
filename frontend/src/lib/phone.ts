import { whatsAppPhone } from './format.ts';

/** Criterio 29: the usual countries, El Salvador first and chosen by default; anything else is «Otro», written with «+» and its code. */
export const phoneCountries = [
  { code: '503', name: 'El Salvador', digits: 8 },
  { code: '502', name: 'Guatemala', digits: 8 },
  { code: '504', name: 'Honduras', digits: 8 },
  { code: '1', name: 'Estados Unidos', digits: 10 },
];
export const OTHER_COUNTRY = 'other';

/** Opens a stored phone (digits; 8 of them are Salvadoran, criterio 27) in its country, or in «Otro» with the whole number. */
export function splitPhone(stored?: string) {
  if (!stored) return { country: '503', local: '' };
  const e164 = whatsAppPhone(stored).slice(1);
  const country = phoneCountries.find(
    (c) => e164.startsWith(c.code) && e164.length === c.code.length + c.digits,
  );
  return country
    ? { country: country.code, local: e164.slice(country.code.length) }
    : { country: OTHER_COUNTRY, local: '+' + e164 };
}

/** The phone the form sends: E.164 for the chosen country, or the stored value when nothing changed (the server then keeps its hash,
 *  criterio 26). Only a help: the server is still the gate. */
export function phoneToSend(
  country: string,
  local: string,
  stored?: string,
): { phone: string } | { error: string } {
  const digits = local.replace(/\D/g, '');
  let phone: string;
  if (country === OTHER_COUNTRY) {
    // Criterio 30: 7, or 9 to 15 digits in all; 8 would read as a Salvadoran number.
    if (!local.trim().startsWith('+') || !/^(\d{7}|\d{9,15})$/.test(digits) || digits[0] === '0')
      return { error: 'Escribe el número con + y su código de país.' };
    phone = '+' + digits;
  } else {
    const chosen = phoneCountries.find((c) => c.code === country) ?? phoneCountries[0];
    if (digits.length !== chosen.digits)
      return { error: `Escribe ${chosen.digits} dígitos para ${chosen.name}.` };
    phone = '+' + chosen.code + digits;
  }
  return { phone: stored && whatsAppPhone(stored) === phone ? stored : phone };
}

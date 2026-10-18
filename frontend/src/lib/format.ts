// Dates and times are the hospital's: its zone and es-SV, never the browser's (MR-23).
export const hospitalLocale = 'es-SV';
export const fallbackZone = 'America/El_Salvador';
export const inZone = (
  value: string | number | Date,
  timeZone: string,
  options: Intl.DateTimeFormatOptions,
) => new Date(value).toLocaleString(hospitalLocale, { ...options, timeZone });
/** The hospital's calendar day as YYYY-MM-DD, to group or compare days. */
export const dayInZone = (value: string | number | Date, timeZone: string) =>
  new Intl.DateTimeFormat('en-CA', { timeZone }).format(new Date(value));
/** MR-44: Hospital's count of the patient's no-shows, as the profile says it; nothing when there are none. */
export const noShowLine = ({
  noShows,
  consecutiveNoShows,
}: {
  noShows: number;
  consecutiveNoShows: number;
}) =>
  noShows > 0
    ? `No asistió ${noShows} ${noShows === 1 ? 'vez' : 'veces'}` +
      (consecutiveNoShows >= 1
        ? ` · ${consecutiveNoShows} ${consecutiveNoShows === 1 ? 'seguida' : 'seguidas'}`
        : '')
    : null;

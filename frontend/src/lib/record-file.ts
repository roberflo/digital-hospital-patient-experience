/** RXE-C1/C2: «Guardar en el expediente» is offered on an inbound image, PDF or DICOM of a contact
 * linked to a Hospital patient, and only to the clinical profile; the server checks all of it again. */
export function canSaveToRecord(
  m: { sender: string; type: string; mediaId?: string; mediaName?: string },
  contact: { patientId?: string | null },
  role: string,
) {
  return (
    role === 'doctor' &&
    !!contact.patientId &&
    m.sender === 'patient' &&
    !!m.mediaId &&
    (m.type === 'image' ||
      (m.type === 'document' && /\.(pdf|dcm|dicom|jpe?g|png|webp)$/i.test(m.mediaName ?? '')))
  );
}
/** Hospital's categories today (05-adjuntos), with its own labels; `radiograph` joins with RXE-A. */
export const recordCategories = [
  { value: 'prior-study', label: 'Estudio previo' },
  { value: 'clinical-photo', label: 'Fotografía clínica' },
  { value: 'referral-letter', label: 'Carta de referencia' },
  { value: 'external-report', label: 'Informe externo' },
  { value: 'other', label: 'Otro' },
];
/** The dialog's values as the endpoint takes them: a date-only clinical date is sent at noon UTC so no zone moves its day. */
export function recordBody(v: Record<string, string>) {
  return {
    categoryCode: v.categoryCode,
    clinicalDate: v.clinicalDate ? `${v.clinicalDate}T12:00:00Z` : null,
    description: v.description?.trim() || null,
  };
}

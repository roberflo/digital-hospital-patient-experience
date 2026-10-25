import { test } from 'node:test';
import assert from 'node:assert/strict';
import { canSaveToRecord, recordBody } from '../src/lib/record-file.ts';

// RXE-C1/C2 (Hospital docs/features/19-odontologia/radiografias-en-el-expediente.spec.md):
// «Guardar en el expediente» only on an inbound image, PDF or DICOM of a linked contact, for the clinical profile.
const image = { sender: 'patient', type: 'image', mediaId: 'media-1' };
const linked = { patientId: 'p-1' };
test('RXE_C1_an_inbound_image_of_a_linked_contact_is_offered_to_a_clinician', () => {
  assert.equal(canSaveToRecord(image, linked, 'doctor'), true);
});
test('RXE_C1_a_pdf_or_dicom_document_is_offered_and_other_documents_are_not', () => {
  for (const name of ['estudio.PDF', 'rx-periapical.dcm', 'rx.dicom', 'foto.jpeg', 'foto.webp'])
    assert.equal(
      canSaveToRecord({ ...image, type: 'document', mediaName: name }, linked, 'doctor'),
      true,
      name,
    );
  for (const name of ['receta.docx', 'notas.txt', undefined])
    assert.equal(
      canSaveToRecord({ ...image, type: 'document', mediaName: name }, linked, 'doctor'),
      false,
      String(name),
    );
});
test('RXE_C1_never_on_outbound_messages_text_audio_or_stickers', () => {
  for (const sender of ['human', 'agent'])
    assert.equal(canSaveToRecord({ ...image, sender }, linked, 'doctor'), false);
  for (const type of ['text', 'audio', 'video', 'sticker'])
    assert.equal(canSaveToRecord({ ...image, type }, linked, 'doctor'), false);
  assert.equal(canSaveToRecord({ ...image, mediaId: undefined }, linked, 'doctor'), false);
});
test('RXE_C1_never_when_the_contact_is_not_linked_to_a_hospital_patient', () => {
  assert.equal(canSaveToRecord(image, {}, 'doctor'), false);
  assert.equal(canSaveToRecord(image, { patientId: null }, 'doctor'), false);
});
test('RXE_C2_only_the_clinical_profile_is_offered_it', () => {
  for (const role of ['agent', 'admin', 'platform', ''])
    assert.equal(canSaveToRecord(image, linked, role), false, role);
});
test('RXE_C3_the_form_sends_the_category_a_noon_utc_clinical_date_and_an_optional_description', () => {
  assert.deepEqual(
    recordBody({ categoryCode: 'prior-study', clinicalDate: '2026-10-20', description: ' RX ' }),
    {
      categoryCode: 'prior-study',
      clinicalDate: '2026-10-20T12:00:00Z',
      description: 'RX',
    },
  );
  assert.deepEqual(recordBody({ categoryCode: 'other', clinicalDate: '', description: '' }), {
    categoryCode: 'other',
    clinicalDate: null,
    description: null,
  });
});

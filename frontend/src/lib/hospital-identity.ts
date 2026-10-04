import { mutate } from 'swr';

// A successful connection check makes the backend copy the hospital's name and zone from Hospital
// (HospitalIdentitySync). These are the reads that paint them: the sidebar (/me), «Mi hospital»
// and the guide (/hospital/connection) and Configuración (/settings).
// Call it from the check's onSuccess: once per answered check, no polling.
export const refreshHospitalIdentity = () =>
  Promise.all(['/me', '/hospital/connection', '/settings'].map((key) => mutate(key)));

'use client';
import useSWR from 'swr';
import { fetcher, type Me } from '@/lib/api';
import { fallbackZone } from '@/lib/format';

/** The hospital's time zone; `/me` is the same request the workspace already makes. */
export const useHospitalZone = () =>
  useSWR<Me>('/me', fetcher).data?.tenant.timeZone ?? fallbackZone;

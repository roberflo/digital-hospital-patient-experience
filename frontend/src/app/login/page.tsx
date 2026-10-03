export const dynamic = 'force-dynamic';
import Login from '@/components/login';
export default function Page() {
  return (
    <Login
      hospitalDemo={!!process.env.DEV_HOSPITAL_TENANT_ID}
      demo={
        process.env.ALLOW_DEV_LOGIN === 'true' &&
        process.env.ASPNETCORE_ENVIRONMENT === 'Development'
      }
    />
  );
}

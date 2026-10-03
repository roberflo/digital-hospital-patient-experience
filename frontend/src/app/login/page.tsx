export const dynamic='force-dynamic';
import Login from '@/components/login';
export default function Page(){return <Login demo={process.env.ALLOW_DEV_LOGIN==='true'&&process.env.ASPNETCORE_ENVIRONMENT==='Development'}/>;}

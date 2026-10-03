import {getServerSession} from 'next-auth';
import {redirect} from 'next/navigation';
import {authOptions} from '@/lib/auth';
import Workspace from '@/components/workspace';
export default async function Page(){const session=await getServerSession(authOptions);if(!session)redirect('/login');return <Workspace/>;}

import { Session, setSession } from './api/client';

/** Which identification the server uses: the X-User switcher ('Header') or a password sign-in ('Jwt'). */
export async function loadAuthMode(): Promise<'Header' | 'Jwt'> {
  try {
    const response = await fetch('/api/auth/mode');
    if (!response.ok) return 'Header';
    const body = (await response.json()) as { mode?: string };
    return body.mode === 'Jwt' ? 'Jwt' : 'Header';
  } catch {
    return 'Header';
  }
}

export type SignInResult = { ok: true } | { ok: false; message: string; field?: 'userName' | 'password' };

/** POST /api/auth/login. On success the token is kept for this browser tab and the page is read again as that user. */
export async function signIn(userName: string, password: string): Promise<SignInResult> {
  let response: Response;
  try {
    response = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ userName, password }),
    });
  } catch {
    return { ok: false, message: 'The server cannot be reached. Try again.' };
  }

  if (response.ok) {
    const body = (await response.json()) as { token: string; expiresAt: string; user: { name: string; role: string } };
    const session: Session = { token: body.token, expiresAt: body.expiresAt, name: body.user.name, role: body.user.role };
    setSession(session);
    return { ok: true };
  }
  if (response.status === 429) return { ok: false, message: 'Too many attempts. Wait a minute and try again.' };
  if (response.status === 400) {
    const problem = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]> };
    const errors = problem.errors ?? {};
    const key = Object.keys(errors).find((k) => k.toLowerCase() === 'username') ?? Object.keys(errors).find((k) => k.toLowerCase() === 'password');
    if (key) return { ok: false, message: errors[key][0], field: key.toLowerCase() === 'username' ? 'userName' : 'password' };
  }
  if (response.status === 401) return { ok: false, message: 'Wrong user name or password.' };
  return { ok: false, message: 'Sign-in failed. Try again.' };
}

export function signOut(): void {
  setSession(null);
  location.reload();
}

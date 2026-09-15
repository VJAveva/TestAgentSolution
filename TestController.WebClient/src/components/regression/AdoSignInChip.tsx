import { useEffect, useState } from 'react';
import { LogIn, ShieldAlert } from 'lucide-react';
import { adoSignedInAs, initAdoAuth, signInToAdo, signOutOfAdo, type AdoAuthConfig } from '../../lib/adoAuth';

/**
 * Delegated Azure DevOps sign-in for the Code Churn view. Lets the user authenticate with their own Entra
 * identity so the server can query ADO as them, instead of depending on the host's PAT/service principal.
 */
export function AdoSignInChip({ onSignedIn }: { onSignedIn: () => void }) {
  const [config, setConfig] = useState<AdoAuthConfig | null>(null);
  const [account, setAccount] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    initAdoAuth()
      .then((cfg) => {
        setConfig(cfg);
        setAccount(adoSignedInAs());
      })
      .catch(() => setConfig(null));
  }, []);

  if (!config?.enabled) return null;

  // The API refuses a delegated token over plain HTTP, so offering sign-in here would only mislead.
  if (!config.secureTransport) {
    return (
      <button
        disabled
        title="Azure DevOps sign-in needs HTTPS - a delegated token must not cross a plaintext connection. Serve this site over TLS to enable it."
        className="inline-flex items-center gap-1.5 px-2.5 h-7 rounded-full border border-acc-amber/40 text-acc-amber bg-acc-amber/10 text-[11px] shrink-0 opacity-70"
      >
        <ShieldAlert size={12} /> ADO sign-in (HTTPS only)
      </button>
    );
  }

  const handle = async () => {
    setBusy(true);
    try {
      if (account) {
        await signOutOfAdo();
        setAccount(null);
      } else {
        setAccount(await signInToAdo());
      }
      onSignedIn();
    } catch {
      setAccount(adoSignedInAs());
    } finally {
      setBusy(false);
    }
  };

  return (
    <button
      onClick={handle}
      disabled={busy}
      title={account ? `Signed in to Azure DevOps as ${account} - click to sign out` : 'Sign in to Azure DevOps with your Microsoft account'}
      className={`inline-flex items-center gap-1.5 px-2.5 h-7 rounded-full border text-[11px] shrink-0 disabled:opacity-40 ${
        account
          ? 'border-acc-green/40 text-acc-green bg-acc-green/10'
          : 'border-bdr text-text-secondary hover:bg-bg-surface hover:text-text-primary'
      }`}
    >
      <LogIn size={12} />
      <span className="truncate max-w-[150px]">
        {busy ? 'Working\u2026' : account ?? 'Sign in to ADO'}
      </span>
    </button>
  );
}

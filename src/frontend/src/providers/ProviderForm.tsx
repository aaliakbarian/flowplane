import { useEffect, useRef, useState, type FormEvent } from 'react'
import { type Configuration, type Provider, type ProviderApi, safeMessage } from './api'

const defaults: Configuration = { providerType: 'ssis', displayName: '', isEnabled: true, configuration: { schemaId: 'ssis.provider-instance/v1', data: { host: '', port: 1433, username: '', trustServerCertificate: false } } }
export function ProviderForm({ api, provider, etag, onSaved, onFailure }: { api: ProviderApi; provider?: Provider; etag?: string; onSaved(id: string): void; onFailure(error: unknown): void }) {
  const [configuration, setConfiguration] = useState<Configuration>(provider ?? defaults)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const password = useRef<HTMLInputElement>(null)
  const replacement = useRef<HTMLInputElement>(null)
  const active = useRef(true)
  useEffect(() => { active.current = true; return () => { active.current = false } }, [])
  function setting(name: keyof Configuration['configuration']['data'], value: string | number | boolean) {
    setConfiguration({ ...configuration, configuration: { ...configuration.configuration, data: { ...configuration.configuration.data, [name]: value } } })
  }
  async function submit(event: FormEvent) {
    event.preventDefault()
    const value = password.current?.value ?? ''
    if (password.current) password.current.value = ''
    setBusy(true); setMessage('')
    try {
      // Explicit non-secret projection: never POST response-only version/identity fields.
      const input: Configuration = { providerType: 'ssis', displayName: configuration.displayName, isEnabled: configuration.isEnabled, configuration: configuration.configuration }
      const reply = provider ? await api.update(provider.id, etag!, input) : await api.create(input, value)
      if (active.current) onSaved(reply.data.id)
    } catch (error) { if (active.current) { onFailure(error); setMessage(safeMessage(error)) } }
    finally { if (active.current) setBusy(false) }
  }
  async function replace(event: FormEvent) {
    event.preventDefault()
    const value = replacement.current?.value ?? ''
    if (replacement.current) replacement.current.value = ''
    setBusy(true); setMessage('')
    try { await api.credential(provider!.id, etag!, value); if (active.current) onSaved(provider!.id) }
    catch (error) { if (active.current) { onFailure(error); setMessage(safeMessage(error)) } }
    finally { if (active.current) setBusy(false) }
  }
  return <section aria-label="Provider configuration">
    <h3>{provider ? 'Edit provider' : 'Create SSIS provider'}</h3>
    <form onSubmit={(event) => void submit(event)}>
      <fieldset disabled={busy}>
        <label>Display name<input value={configuration.displayName} required maxLength={200} onChange={(event) => setConfiguration({ ...configuration, displayName: event.target.value })} /></label>
        <label>Host<input value={configuration.configuration.data.host} required onChange={(event) => setting('host', event.target.value)} /></label>
        <label>Port<input type="number" min={1} max={65535} value={configuration.configuration.data.port} required onChange={(event) => setting('port', Number(event.target.value))} /></label>
        <label>Username<input value={configuration.configuration.data.username} required onChange={(event) => setting('username', event.target.value)} /></label>
        <label><input type="checkbox" checked={configuration.isEnabled} onChange={(event) => setConfiguration({ ...configuration, isEnabled: event.target.checked })} />Enabled</label>
        <label><input type="checkbox" checked={configuration.configuration.data.trustServerCertificate} onChange={(event) => setting('trustServerCertificate', event.target.checked)} />Trust server certificate (explicit Development-only exception)</label>
        <p>SQL encryption is mandatory. Hosts and ports must be allowed by the backend operator.</p>
        {!provider && <label>Password<input ref={password} type="password" autoComplete="new-password" required maxLength={4096} /></label>}
        <button type="submit">{provider ? 'Save settings' : 'Create provider'}</button>
      </fieldset>
    </form>
    {provider && <form onSubmit={(event) => void replace(event)}><fieldset disabled={busy}>
      <label>New password<input ref={replacement} type="password" autoComplete="new-password" required maxLength={4096} /></label>
      <button type="submit">Replace password</button>
    </fieldset></form>}
    {message && <p role="alert">{message}</p>}
  </section>
}

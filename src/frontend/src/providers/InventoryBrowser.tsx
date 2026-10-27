import { useEffect, useRef, useState } from 'react'
import { ApiError, type Inventory, type ProviderApi, safeMessage } from './api'

const fields = ['folderName','projectName','packageName','folderId','projectId','packageId','isEntryPoint','compatibility','projectFormatVersion','packageFormatVersion','activeObjectVersionLsn','projectObjectVersionLsn','lastDeployedAt','projectLastDeployedAt','packageGuid','packageVersionMajor','packageVersionMinor','packageVersionBuild','packageVersionGuid','scope','name','dataType','isRequired','isSensitive','valueSource','isValueSet','referencedVariableName','referenceId','referenceType','environmentFolderName','environmentName']
export function InventoryBrowser({ api, id, onFailure }: { api: ProviderApi; id: string; onFailure(error: unknown): void }) {
  const [view, setView] = useState('candidates')
  const [page, setPage] = useState<Inventory | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const request = useRef<AbortController | null>(null)
  useEffect(() => () => request.current?.abort(), [])
  async function load(cursor?: string) {
    request.current?.abort()
    const controller = new AbortController(); request.current = controller
    setPage(null); setError(''); setBusy(true)
    try {
      let result: Inventory
      try { result = (await api.inventory(id, view, cursor, controller.signal)).data }
      catch (failure) {
        if (cursor && failure instanceof ApiError && ['InventoryInvalidCursor','InventoryGenerationChanged'].includes(failure.code))
          result = (await api.inventory(id, view, undefined, controller.signal)).data
        else throw failure
      }
      if (!controller.signal.aborted) {
        if (result.providerInstanceId !== id || !Array.isArray(result.items)) throw new ApiError(502)
        setPage(result)
      }
    } catch (failure) { if (!controller.signal.aborted) { setError(safeMessage(failure)); onFailure(failure) } }
    finally { if (!controller.signal.aborted) setBusy(false) }
  }
  return <section aria-label="Published inventory"><h3>Current published inventory</h3>
    <label>Inventory view<select value={view} onChange={(event) => { request.current?.abort(); setPage(null); setError(''); setBusy(false); setView(event.target.value) }}>
      <option value="candidates">Entry-point candidates</option><option value="all">All observations</option>
      <option value="folder">Folders</option><option value="project">Projects</option><option value="package">Packages</option><option value="parameter">Parameters</option><option value="environment-reference">Environment references</option>
    </select></label>
    <button disabled={busy} onClick={() => void load()}>Load inventory</button>
    {busy && <p role="status">Checking current native visibility…</p>}{error && <p role="alert">{error}</p>}
    {page && <><p>Published generation: {page.generationId}</p>
      {page.items.length === 0 && <p>No visible observations match this view. This is a complete empty page.</p>}
      {page.items.map((item, index) => <article key={index}><h4>{item.executableReference ? 'Eligible entry-point candidate' : 'Observed metadata'}</h4>
        <dl>{fields.flatMap((field) => {
          const value = item.metadata[field]
          return typeof value === 'string' || typeof value === 'boolean' || typeof value === 'number'
            ? [<div key={field}><dt>{field}</dt><dd>{String(value)}</dd></div>] : []
        })}</dl>
      </article>)}
      {page.nextCursor && <button disabled={busy} onClick={() => void load(page.nextCursor!)}>Next inventory page</button>}
    </>}
    <p>Candidate eligibility does not guarantee permission to execute or successful package execution.</p>
  </section>
}

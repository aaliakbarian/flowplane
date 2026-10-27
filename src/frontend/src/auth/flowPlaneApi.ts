export interface FlowPlaneIdentity {
  subject: string
  name: string | null
}

type Fetcher = (
  input: RequestInfo | URL,
  init?: RequestInit,
) => Promise<Response>

export class IdentityRequestError extends Error {
  readonly status: number
  constructor(status: number) {
    super(`The identity request failed with status ${status}`)
    this.status = status
  }
}

export async function getCurrentIdentity(
  accessToken: string,
  fetcher: Fetcher = fetch,
): Promise<FlowPlaneIdentity> {
  if (accessToken.length === 0) {
    throw new Error('An access token is required')
  }

  const response = await fetcher('/api/me', {
    headers: { Authorization: `Bearer ${accessToken}` },
  })

  if (!response.ok) {
    throw new IdentityRequestError(response.status)
  }

  return response.json() as Promise<FlowPlaneIdentity>
}

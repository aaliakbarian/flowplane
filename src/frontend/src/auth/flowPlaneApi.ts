export interface FlowPlaneIdentity {
  subject: string
  name: string | null
}

type Fetcher = (
  input: RequestInfo | URL,
  init?: RequestInit,
) => Promise<Response>

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
    throw new Error(`The identity request failed with status ${response.status}`)
  }

  return response.json() as Promise<FlowPlaneIdentity>
}

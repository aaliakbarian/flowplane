namespace FlowPlane.Agent.Protocol;

// Public control-plane binding only; never contains endpoint addresses, credential locators or execution authority.
public sealed record AgentProviderBinding(Guid ProviderInstanceId, long ProviderVersion);
public sealed record AgentAssignmentsReceipt(string Schema, Guid AgentId, AgentProviderBinding[] Providers);
